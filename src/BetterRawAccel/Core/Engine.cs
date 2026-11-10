using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BetterRawAccel.Core
{
    /// <summary>
    /// Owns the settings being edited and the settings in the driver, and does every calculation
    /// through Raw Accel's own wrapper.dll so the plot matches what the driver will do.
    /// All members must be called on the UI thread.
    /// </summary>
    public sealed class Engine
    {
        public const int CurvePoints = 240;
        public const int ThumbPoints = 49;
        const int MaxUndo = 100;

        readonly ManagedAccel preview = new ManagedAccel();
        readonly List<string> undo = new List<string>();
        readonly List<string> redo = new List<string>();
        string activeJson;
        string lastTag;
        DateTime lastPush = DateTime.MinValue;

        public Engine(AppSettings app)
        {
            App = app;
        }

        public AppSettings App { get; }
        public DriverConfig Edit { get; private set; }
        public DriverConfig Active { get; private set; }
        public int Sel { get; private set; }
        public Version DriverVersion { get; private set; }
        public bool Writing { get; private set; }
        public DateTime? WrittenAt { get; private set; }
        public JObject Meta { get; private set; }

        public Profile Current => Edit.profiles[Sel];
        public double MaxSpeed => App.PlotDpi * 0.05;
        public bool Pending => Edit.ToJSON() != activeJson;
        public bool CanUndo => undo.Count > 0;
        public bool CanRedo => redo.Count > 0;
        public SpeedHistogram Histogram { get; } = new SpeedHistogram();
        public ReferenceCurveData PinnedCurve { get; private set; }

        public void PinReferenceCurve(string profileNameOrSource = null)
        {
            Profile p;
            string label;
            if (string.IsNullOrEmpty(profileNameOrSource) || profileNameOrSource == "editing")
            {
                p = Current;
                label = Current.name + " (editing)";
            }
            else if (profileNameOrSource == "driver")
            {
                p = DriverTwin() ?? Active.profiles[0];
                label = p.name + " (driver)";
            }
            else
            {
                p = Edit.profiles.FirstOrDefault(x => string.Equals(x.name, profileNameOrSource, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Profile not found: " + profileNameOrSource);
                label = p.name;
            }

            if (Validate(p) != null) throw new InvalidOperationException("Could not calculate curve for: " + label);
            bool whole = p.inputSpeedArgs.combineMagnitudes;
            PinnedCurve = new ReferenceCurveData
            {
                Name = label,
                Points = Sample(p, CurvePoints, MaxSpeed, false),
                PointsY = whole ? null : Sample(p, CurvePoints, MaxSpeed, true),
                MaxSpeed = MaxSpeed,
                PinnedAt = DateTime.UtcNow
            };
        }

        public void ClearReferenceCurve()
        {
            PinnedCurve = null;
        }

        // ---------------------------------------------------------------- start-up

        /// <summary>Throws InteropException when the driver is missing or the wrong version.</summary>
        public void Init()
        {
            DriverVersion = VersionHelper.ValidOrThrow();
            Meta = JObject.Parse(File.ReadAllText(Path.Combine(Paths.UiDir, "meta.json")));
            ReadActive();
            Edit = LoadUserSettings() ?? Parse(activeJson);
            Sel = 0;
        }

        void ReadActive()
        {
            var cfg = DriverConfig.GetActive();
            if (cfg.profiles.Count == 0)
            {
                var p = new Profile();
                cfg.profiles.Add(p);
                cfg.accels.Add(new ManagedAccel(p));
            }
            Active = cfg;
            activeJson = cfg.ToJSON();
        }

        static DriverConfig LoadUserSettings()
        {
            try
            {
                if (File.Exists(Paths.SettingsFile)) return Parse(File.ReadAllText(Paths.SettingsFile));
            }
            catch (Exception e)
            {
                Log.Error("settings.json ignored", e);
            }
            return null;
        }

        static DriverConfig Parse(string json)
        {
            Tuple<DriverConfig, string> t;
            try
            {
                t = DriverConfig.Convert(json);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("Settings could not be read: " + e.Message);
            }
            if (t.Item1 == null) throw new InvalidOperationException(t.Item2.Trim());
            return t.Item1;
        }

        // ---------------------------------------------------------------- editing

        void PushUndo(string before, string tag)
        {
            var now = DateTime.UtcNow;
            bool coalesce = tag != null && tag == lastTag && (now - lastPush).TotalMilliseconds < 600 && undo.Count > 0;
            if (!coalesce)
            {
                undo.Add(before);
                if (undo.Count > MaxUndo) undo.RemoveAt(0);
            }
            redo.Clear();
            lastTag = tag;
            lastPush = now;
        }

        /// <summary>Runs a change against the config. If the result is invalid, everything is put back and the reason is thrown.</summary>
        void Mutate(string tag, Action change)
        {
            var before = Edit.ToJSON();
            int sel = Sel;
            try
            {
                change();
                var err = Edit.Errors();
                if (err != null) throw new InvalidOperationException(Tidy(err));
            }
            catch
            {
                Edit = Parse(before);
                Sel = Math.Min(sel, Edit.profiles.Count - 1);
                throw;
            }
            if (Edit.ToJSON() != before) PushUndo(before, tag);
        }

        static string Tidy(string errors) =>
            string.Join("; ", errors.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Distinct());

        public void UpdateProfile(JObject patch)
        {
            var tag = "p:" + string.Join(",", patch.Properties().Select(p => p.Name + (p.Value is JObject o ? "." + string.Join("+", o.Properties().Select(q => q.Name)) : "")));
            Mutate(tag, () =>
            {
                var p = Current;
                if (patch["name"] != null) CheckName((string)patch["name"], Sel);
                PatchProfile(p, patch);
                Edit.SetProfileAt(Sel, p);
            });
        }

        void CheckName(string name, int except)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Profile name can not be empty");
            if (name.Length >= 256) throw new InvalidOperationException("Profile name is too long");
            for (int i = 0; i < Edit.profiles.Count; i++)
                if (i != except && string.Equals(Edit.profiles[i].name, name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"A profile named \"{name}\" already exists");
        }

        public int FindProfile(JToken which)
        {
            if (which == null || which.Type == JTokenType.Null) return Sel;
            if (which.Type == JTokenType.Integer)
            {
                int i = (int)which;
                if (i < 0 || i >= Edit.profiles.Count) throw new InvalidOperationException("No profile at index " + i);
                return i;
            }
            var name = (string)which;
            int idx = Edit.profiles.FindIndex(p => string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new InvalidOperationException($"No profile named \"{name}\"");
            return idx;
        }

        public void SelectProfile(JToken which) => Sel = FindProfile(which);

        public void CreateProfile(string name, JToken copyFrom)
        {
            Mutate(null, () =>
            {
                CheckName(name, -1);
                Profile p;
                if (copyFrom != null && copyFrom.Type != JTokenType.Null)
                {
                    // Round-trip through the DTO to get an independent copy.
                    p = new Profile();
                    PatchProfile(p, ToDto(Edit.profiles[FindProfile(copyFrom)]));
                }
                else p = new Profile();
                p.name = name;
                Edit.profiles.Add(p);
                Edit.accels.Add(new ManagedAccel(p));
                Sel = Edit.profiles.Count - 1;
            });
        }

        public void DeleteProfile(JToken which)
        {
            Mutate(null, () =>
            {
                int i = FindProfile(which);
                if (i == 0) throw new InvalidOperationException("The first profile is the default for every device and can not be deleted");
                var name = Edit.profiles[i].name;
                Edit.profiles.RemoveAt(i);
                Edit.accels.RemoveAt(i);
                foreach (var d in Edit.devices.Where(d => d.profile == name)) d.profile = "";
                Sel = Math.Min(Sel, Edit.profiles.Count - 1);
                if (Sel == i) Sel = 0;
            });
        }

        public void RenameProfile(JToken which, string name)
        {
            Mutate(null, () =>
            {
                int i = FindProfile(which);
                CheckName(name, i);
                var p = Edit.profiles[i];
                var old = p.name;
                p.name = name;
                Edit.SetProfileAt(i, p);
                foreach (var d in Edit.devices.Where(d => d.profile == old)) d.profile = name;
            });
        }

        public void SetDefaultDevice(JObject patch)
        {
            Mutate("dev:default", () => Edit.defaultDeviceConfig = PatchDevice(Edit.defaultDeviceConfig, patch));
        }

        public void SetDevice(string id, JObject patch)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Device id is required");
            Mutate("dev:" + id, () =>
            {
                var d = Edit.devices.Find(x => x.id == id);
                if (d == null)
                {
                    var sys = MultiHandleDevice.GetList().FirstOrDefault(x => x.id == id);
                    d = new DeviceSettings { id = id, name = sys?.name ?? (string)patch["name"] ?? "", profile = "", config = Edit.defaultDeviceConfig };
                    Edit.devices.Add(d);
                }
                if (patch["profile"] != null)
                {
                    var prof = (string)patch["profile"] ?? "";
                    if (prof != "" && !Edit.profiles.Any(p => p.name == prof)) throw new InvalidOperationException($"No profile named \"{prof}\"");
                    d.profile = prof;
                }
                d.config = PatchDevice(d.config, patch);
            });
        }

        public void RemoveDevice(string id)
        {
            Mutate(null, () => Edit.devices.RemoveAll(d => d.id == id));
        }

        static DeviceConfig PatchDevice(DeviceConfig c, JObject j)
        {
            foreach (var kv in j)
            {
                switch (kv.Key)
                {
                    case "dpi": c.dpi = (int)kv.Value; break;
                    case "pollingRate": c.pollingRate = (int)kv.Value; break;
                    case "disable": c.disable = (bool)kv.Value; break;
                    case "pollTimeLock": c.pollTimeLock = (bool)kv.Value; break;
                    case "profile": case "name": case "id": break;
                    default: throw new InvalidOperationException("Unknown device field: " + kv.Key);
                }
            }
            if (c.dpi < 0 || c.dpi > 100000) throw new InvalidOperationException("DPI must be 0 (do not normalise) or a positive number");
            if (c.pollingRate != 0 && (c.pollingRate < 125 || c.pollingRate > 8000))
                throw new InvalidOperationException("Polling rate must be 0 (automatic) or between 125 and 8000 Hz");
            return c;
        }

        public void Undo()
        {
            if (undo.Count == 0) return;
            redo.Add(Edit.ToJSON());
            Restore(undo[undo.Count - 1]);
            undo.RemoveAt(undo.Count - 1);
        }

        public void Redo()
        {
            if (redo.Count == 0) return;
            undo.Add(Edit.ToJSON());
            Restore(redo[redo.Count - 1]);
            redo.RemoveAt(redo.Count - 1);
        }

        void Restore(string json)
        {
            var name = Current.name;
            Edit = Parse(json);
            int i = Edit.profiles.FindIndex(p => p.name == name);
            Sel = i >= 0 ? i : Math.Min(Sel, Edit.profiles.Count - 1);
            lastTag = null;
        }

        /// <summary>Throw away unwritten edits and go back to what the driver has.</summary>
        public void Revert()
        {
            var before = Edit.ToJSON();
            if (before == activeJson) return;
            Restore(activeJson);
            PushUndo(before, null);
        }

        public string Export() => Edit.ToJSON();

        public void Import(string json)
        {
            var cfg = Parse(json);
            var before = Edit.ToJSON();
            Edit = cfg;
            Sel = 0;
            if (Edit.ToJSON() != before) PushUndo(before, null);
        }

        // ---------------------------------------------------------------- driver

        public async Task ApplyAsync()
        {
            if (Writing) throw new InvalidOperationException("A write is already in progress");
            var err = Edit.Errors();
            if (err != null) throw new InvalidOperationException(Tidy(err));

            var json = Edit.ToJSON();
            var cfg = Parse(json);
            Writing = true;
            try
            {
                await Task.Run(() => cfg.Activate());
                // The driver holds new settings for this long before using them (its anti-abuse delay).
                await Task.Delay((int)DriverConfig.WriteDelayMs);
                Active = cfg;
                activeJson = json;
                WrittenAt = DateTime.Now;
                AtomicFile.WriteAllText(Paths.SettingsFile, json);
            }
            finally
            {
                Writing = false;
            }
        }

        /// <summary>Puts the driver back to no acceleration. The edited settings are kept, so they show as unwritten.</summary>
        public async Task ResetDriverAsync()
        {
            if (Writing) throw new InvalidOperationException("A write is already in progress");
            Writing = true;
            try
            {
                await Task.Run(() => DriverConfig.Deactivate());
                await Task.Delay((int)DriverConfig.WriteDelayMs);
                ReadActive();
                WrittenAt = DateTime.Now;
            }
            finally
            {
                Writing = false;
            }
        }

        // ---------------------------------------------------------------- curves

        public static string Validate(Profile p)
        {
            var e = new ProfileErrors(new List<Profile> { p });
            return e.Empty() ? null : Tidy(e.ToString());
        }

        /// <summary>
        /// Sensitivity (output speed / input speed) at evenly spaced input speeds, from the driver's own modifier.
        /// Each sample is a whole number of counts over the time that gives the wanted speed, as the driver would see it.
        /// </summary>
        double[] Sample(Profile p, int n, double maxSpeed, bool yAxis)
        {
            preview.Settings = p;
            // Smoothing is stateful and would smear a static plot, so it is switched off for graphing.
            var q = preview.Settings;
            var s = q.inputSpeedArgs;
            s.inputSmoothHalflife = 0; s.scaleSmoothHalflife = 0; s.outputSmoothHalflife = 0;
            q.inputSpeedArgs = s;
            preview.Settings = q;

            var res = new double[n];
            for (int i = 0; i < n; i++)
            {
                double v = Math.Max(maxSpeed * i / (n - 1), maxSpeed / (n - 1) * 0.02);
                res[i] = SensAt(v, yAxis);
            }
            return res;
        }

        double SensAt(double v, bool yAxis)
        {
            int counts = Math.Max(1, (int)Math.Ceiling(v));
            double time = counts / v;
            var o = yAxis ? preview.Accelerate(0, counts, 1, time) : preview.Accelerate(counts, 0, 1, time);
            double sens = Math.Sqrt(o.Item1 * o.Item1 + o.Item2 * o.Item2) / time / v;
            if (double.IsNaN(sens) || double.IsInfinity(sens)) return 0;
            return Math.Min(sens, 1e6);
        }

        public double Evaluate(Profile p, double speed, bool yAxis)
        {
            if (speed <= 0) throw new InvalidOperationException("Speed must be greater than 0");
            var err = Validate(p);
            if (err != null) throw new InvalidOperationException(err);
            return Sample(p, 2, speed, yAxis)[1];
        }

        public static JArray Arr(double[] a) => a == null ? null : new JArray(a.Select(v => (object)Math.Round(v, 5)));

        public JObject Curve(Profile p, int n, double maxSpeed)
        {
            if (Validate(p) != null) return null;
            bool whole = p.inputSpeedArgs.combineMagnitudes;
            return new JObject
            {
                ["x"] = Arr(Sample(p, n, maxSpeed, false)),
                ["y"] = whole ? null : Arr(Sample(p, n, maxSpeed, true)),
            };
        }

        /// <summary>The profile in the driver that corresponds to the one being edited, or null.</summary>
        public Profile DriverTwin()
        {
            var name = Current.name;
            return Active.profiles.FirstOrDefault(p => p.name == name);
        }

        JObject Thumbs()
        {
            var res = new JObject();
            var p = Current;
            var keep = p.argsX;
            try
            {
                foreach (JObject m in (JArray)Meta["modes"])
                {
                    var a = PatchArgs(keep, new JObject { ["mode"] = (string)m["id"] });
                    p.argsX = a;
                    res[(string)m["id"]] = Validate(p) == null ? Arr(Sample(p, ThumbPoints, MaxSpeed, false)) : null;
                }
            }
            finally
            {
                p.argsX = keep;
            }
            return res;
        }

        double[] liveCurve;
        string liveKey;
        const int LivePoints = 480;

        /// <summary>
        /// Raw input reaches user mode after the driver has changed it, so what we measure is output speed.
        /// This works back to the hand speed that produces it, using the curve that is active in the driver.
        /// </summary>
        public double InputFromOutput(double outSpeed)
        {
            if (outSpeed <= 0) return 0;
            var twin = DriverTwin() ?? Active.profiles[0];
            double max = MaxSpeed * 2;
            var key = activeJson + "|" + twin.name + "|" + max;
            if (key != liveKey)
            {
                liveCurve = Validate(twin) == null ? Sample(twin, LivePoints, max, false) : null;
                liveKey = key;
            }
            if (liveCurve == null) return outSpeed;

            double prevV = 0, prevOut = 0;
            for (int i = 1; i < LivePoints; i++)
            {
                double v = max * i / (LivePoints - 1), o = v * liveCurve[i];
                if (o >= outSpeed)
                {
                    double span = o - prevOut;
                    return span <= 0 ? v : prevV + (v - prevV) * (outSpeed - prevOut) / span;
                }
                prevV = v; prevOut = o;
            }
            double last = liveCurve[LivePoints - 1];
            return last > 0 ? outSpeed / last : outSpeed;
        }

        // ---------------------------------------------------------------- state for the UI and for MCP

        public JObject UiState(bool lite)
        {
            var p = Current;
            var err = Validate(p);
            var st = new JObject
            {
                ["profile"] = ToDto(p),
                ["sel"] = Sel,
                ["errors"] = err,
                ["pending"] = Pending,
                ["writing"] = Writing,
                ["canUndo"] = CanUndo,
                ["canRedo"] = CanRedo,
                ["maxSpeed"] = MaxSpeed,
                ["curve"] = Curve(p, CurvePoints, MaxSpeed),
                ["pinnedCurve"] = PinnedCurve?.ToDto(),
                ["histogram"] = Histogram.ToDto(MaxSpeed),
            };
            if (lite) return st;

            var twin = DriverTwin();
            st["meta"] = Meta;
            st["profiles"] = new JArray(Edit.profiles.Select(x => x.name));
            st["driverProfiles"] = new JArray(Active.profiles.Select(x => x.name));
            st["driverProfile"] = twin == null ? null : ToDto(twin);
            st["driverCurve"] = twin == null ? null : Curve(twin, CurvePoints, MaxSpeed);
            st["thumbs"] = Thumbs();
            st["devices"] = Devices();
            st["writtenAt"] = WrittenAt?.ToString("HH:mm:ss");
            st["app"] = new JObject
            {
                ["name"] = AppInfo.Name,
                ["version"] = AppInfo.Version.ToString(3),
                ["dev"] = AppInfo.IsDevBuild,
                ["driverVersion"] = DriverVersion.ToString(3),
                ["repo"] = AppInfo.Repo,
                ["upstream"] = AppInfo.UpstreamRepo,
                ["writeDelayMs"] = DriverConfig.WriteDelayMs,
            };
            st["settings"] = SettingsDto();
            return st;
        }

        public JObject SettingsDto() => new JObject
        {
            ["theme"] = App.Theme,
            ["font"] = App.Font,
            ["autoUpdate"] = App.AutoUpdate,
            ["applyOnStart"] = App.ApplyOnStart,
            ["mcpEnabled"] = App.McpEnabled,
            ["mcpPort"] = App.McpPort,
            ["plotDpi"] = App.PlotDpi,
            ["plotPollRate"] = App.PlotPollRate,
            ["helpSeen"] = App.HelpSeen,
            ["plotView"] = App.PlotView,
        };

        /// <summary>Compact summary for MCP clients: no curve arrays, no metadata.</summary>
        public JObject Summary() => new JObject
        {
            ["selectedProfile"] = Current.name,
            ["profiles"] = new JArray(Edit.profiles.Select(x => x.name)),
            ["profile"] = ToDto(Current, compact: true),
            ["unwrittenChanges"] = Pending,
            ["errors"] = Validate(Current),
            ["driverVersion"] = DriverVersion.ToString(3),
            ["appVersion"] = AppInfo.Version.ToString(3),
            ["lastWrite"] = WrittenAt?.ToString("HH:mm:ss"),
            ["canUndo"] = CanUndo,
            ["canRedo"] = CanRedo,
        };

        public JObject Devices()
        {
            var connected = MultiHandleDevice.GetList();
            var list = new JArray();
            var seen = new HashSet<string>();
            foreach (var d in connected)
            {
                seen.Add(d.id);
                list.Add(DeviceDto(d.id, d.name, true, Edit.devices.Find(x => x.id == d.id)));
            }
            foreach (var d in Edit.devices.Where(d => !seen.Contains(d.id)))
                list.Add(DeviceDto(d.id, d.name, false, d));
            return new JObject
            {
                ["default"] = ConfigDto(Edit.defaultDeviceConfig),
                ["list"] = list,
            };
        }

        static JObject DeviceDto(string id, string name, bool connected, DeviceSettings s)
        {
            var o = s == null ? new JObject() : ConfigDto(s.config);
            o["id"] = id;
            o["name"] = string.IsNullOrWhiteSpace(name) ? (s?.name ?? "") : name;
            o["connected"] = connected;
            o["override"] = s != null;
            o["profile"] = s?.profile ?? "";
            return o;
        }

        static JObject ConfigDto(DeviceConfig c) => new JObject
        {
            ["dpi"] = c.dpi,
            ["pollingRate"] = c.pollingRate,
            ["disable"] = c.disable,
            ["pollTimeLock"] = c.pollTimeLock,
        };

        // ---------------------------------------------------------------- profile <-> plain JSON

        public static JObject ToDto(Profile p, bool compact = false) => new JObject
        {
            ["name"] = p.name,
            ["whole"] = p.inputSpeedArgs.combineMagnitudes,
            ["x"] = ArgsDto(p.argsX, compact),
            ["y"] = ArgsDto(p.argsY, compact),
            ["sens"] = p.outputDPI / 1000.0,
            ["yxRatio"] = p.yxOutputDPIRatio,
            ["lrRatio"] = p.lrOutputDPIRatio,
            ["udRatio"] = p.udOutputDPIRatio,
            ["rotation"] = p.rotation,
            ["snap"] = p.snap,
            ["speedCap"] = p.maximumSpeed,
            ["lpNorm"] = p.inputSpeedArgs.lpNorm,
            ["inputSmooth"] = p.inputSpeedArgs.inputSmoothHalflife,
            ["scaleSmooth"] = p.inputSpeedArgs.scaleSmoothHalflife,
            ["outputSmooth"] = p.inputSpeedArgs.outputSmoothHalflife,
            ["domainX"] = p.domainXY.x,
            ["domainY"] = p.domainXY.y,
            ["rangeX"] = p.rangeXY.x,
            ["rangeY"] = p.rangeXY.y,
        };

        static JObject ArgsDto(AccelArgs a, bool compact)
        {
            var o = new JObject
            {
                ["mode"] = a.mode.ToString(),
                ["gain"] = a.gain,
                ["inputOffset"] = a.inputOffset,
                ["outputOffset"] = a.outputOffset,
                ["acceleration"] = a.acceleration,
                ["decayRate"] = a.decayRate,
                ["gamma"] = a.gamma,
                ["motivity"] = a.motivity,
                ["exponentClassic"] = a.exponentClassic,
                ["scale"] = a.scale,
                ["exponentPower"] = a.exponentPower,
                ["limit"] = a.limit,
                ["syncSpeed"] = a.syncSpeed,
                ["smooth"] = a.smooth,
                ["capX"] = a.cap.x,
                ["capY"] = a.cap.y,
                ["capMode"] = a.capMode.ToString(),
            };
            if (!compact || a.mode == AccelMode.lut)
            {
                var pts = new JArray();
                int n = a.data == null ? 0 : Math.Min(a.length, a.data.Length) / 2;
                for (int i = 0; i < n; i++) pts.Add(new JArray(Math.Round(a.data[i * 2], 5), Math.Round(a.data[i * 2 + 1], 5)));
                o["lut"] = pts;
            }
            return o;
        }

        static double Num(JToken t, string field)
        {
            if (t == null || (t.Type != JTokenType.Float && t.Type != JTokenType.Integer))
                throw new InvalidOperationException($"\"{field}\" must be a number");
            double v = (double)t;
            if (double.IsNaN(v) || double.IsInfinity(v)) throw new InvalidOperationException($"\"{field}\" must be a finite number");
            return v;
        }

        static bool Flag(JToken t, string field)
        {
            if (t == null || t.Type != JTokenType.Boolean) throw new InvalidOperationException($"\"{field}\" must be true or false");
            return (bool)t;
        }

        public static void PatchProfile(Profile p, JObject j)
        {
            foreach (var kv in j)
            {
                var k = kv.Key; var v = kv.Value;
                switch (k)
                {
                    case "name": p.name = (string)v; break;
                    case "x": p.argsX = PatchArgs(p.argsX, AsObject(v, k)); break;
                    case "y": p.argsY = PatchArgs(p.argsY, AsObject(v, k)); break;
                    case "sens": p.outputDPI = Num(v, k) * 1000.0; break;
                    case "yxRatio": p.yxOutputDPIRatio = Num(v, k); break;
                    case "lrRatio": p.lrOutputDPIRatio = Num(v, k); break;
                    case "udRatio": p.udOutputDPIRatio = Num(v, k); break;
                    case "rotation": p.rotation = Num(v, k); break;
                    case "snap": p.snap = Num(v, k); break;
                    case "speedCap": p.maximumSpeed = Num(v, k); break;
                    case "whole": { var s = p.inputSpeedArgs; s.combineMagnitudes = Flag(v, k); p.inputSpeedArgs = s; break; }
                    case "lpNorm": { var s = p.inputSpeedArgs; s.lpNorm = Num(v, k); p.inputSpeedArgs = s; break; }
                    case "inputSmooth": { var s = p.inputSpeedArgs; s.inputSmoothHalflife = NonNeg(v, k); p.inputSpeedArgs = s; break; }
                    case "scaleSmooth": { var s = p.inputSpeedArgs; s.scaleSmoothHalflife = NonNeg(v, k); p.inputSpeedArgs = s; break; }
                    case "outputSmooth": { var s = p.inputSpeedArgs; s.outputSmoothHalflife = NonNeg(v, k); p.inputSpeedArgs = s; break; }
                    case "domainX": { var d = p.domainXY; d.x = Num(v, k); p.domainXY = d; break; }
                    case "domainY": { var d = p.domainXY; d.y = Num(v, k); p.domainXY = d; break; }
                    case "rangeX": { var d = p.rangeXY; d.x = Num(v, k); p.rangeXY = d; break; }
                    case "rangeY": { var d = p.rangeXY; d.y = Num(v, k); p.rangeXY = d; break; }
                    default: throw new InvalidOperationException("Unknown profile field: " + k);
                }
            }
        }

        static double NonNeg(JToken t, string field)
        {
            double v = Num(t, field);
            if (v < 0) throw new InvalidOperationException($"\"{field}\" can not be negative");
            return v;
        }

        static JObject AsObject(JToken t, string field) =>
            t as JObject ?? throw new InvalidOperationException($"\"{field}\" must be an object");

        public static AccelArgs PatchArgs(AccelArgs a, JObject j)
        {
            foreach (var kv in j)
            {
                var k = kv.Key; var v = kv.Value;
                switch (k)
                {
                    case "mode":
                        switch (((string)v ?? "").ToLowerInvariant())
                        {
                            case "off": case "noaccel": a.mode = AccelMode.noaccel; break;
                            case "linear": a.mode = AccelMode.classic; a.exponentClassic = 2; break;
                            case "classic": a.mode = AccelMode.classic; break;
                            case "natural": a.mode = AccelMode.natural; break;
                            case "power": a.mode = AccelMode.power; break;
                            case "synchronous": case "sync": a.mode = AccelMode.synchronous; break;
                            case "jump": a.mode = AccelMode.jump; break;
                            case "lut": case "table": a.mode = AccelMode.lut; break;
                            default: throw new InvalidOperationException("Unknown mode: " + v + ". Use off, linear, classic, natural, power, synchronous, jump or lut");
                        }
                        break;
                    case "gain": a.gain = Flag(v, k); break;
                    case "inputOffset": a.inputOffset = Num(v, k); break;
                    case "outputOffset": a.outputOffset = Num(v, k); break;
                    case "acceleration": a.acceleration = Num(v, k); break;
                    case "decayRate": a.decayRate = Num(v, k); break;
                    case "gamma": a.gamma = Num(v, k); break;
                    case "motivity": a.motivity = Num(v, k); break;
                    case "exponentClassic": a.exponentClassic = Num(v, k); break;
                    case "scale": a.scale = Num(v, k); break;
                    case "exponentPower": a.exponentPower = Num(v, k); break;
                    case "limit": a.limit = Num(v, k); break;
                    case "syncSpeed": a.syncSpeed = Num(v, k); break;
                    case "smooth": a.smooth = Num(v, k); break;
                    case "capX": { var c = a.cap; c.x = Num(v, k); a.cap = c; break; }
                    case "capY": { var c = a.cap; c.y = Num(v, k); a.cap = c; break; }
                    case "capMode":
                        if (!Enum.TryParse((string)v, true, out CapMode cm)) throw new InvalidOperationException("capMode must be output, input or in_out");
                        a.capMode = cm;
                        break;
                    case "lut":
                        {
                            var pts = v as JArray ?? throw new InvalidOperationException("lut must be an array of [speed, value] pairs");
                            if (pts.Count > AccelArgs.MaxLutPoints) throw new InvalidOperationException($"Too many points (max {AccelArgs.MaxLutPoints})");
                            var data = new float[AccelArgs.MaxLutPoints * 2];
                            double last = double.NegativeInfinity;
                            for (int i = 0; i < pts.Count; i++)
                            {
                                var pt = pts[i] as JArray;
                                if (pt == null || pt.Count != 2) throw new InvalidOperationException("Each point must be [speed, value]");
                                double px = Num(pt[0], "lut speed"), py = Num(pt[1], "lut value");
                                if (px <= last) throw new InvalidOperationException("Point speeds must increase from one point to the next");
                                if (px < 0 || py < 0) throw new InvalidOperationException("Points can not be negative");
                                last = px;
                                data[i * 2] = (float)px;
                                data[i * 2 + 1] = (float)py;
                            }
                            a.data = data;
                            a.length = pts.Count * 2;
                        }
                        break;
                    default: throw new InvalidOperationException("Unknown accel field: " + k);
                }
            }
            if (a.data == null || a.data.Length != AccelArgs.MaxLutPoints * 2)
            {
                var data = new float[AccelArgs.MaxLutPoints * 2];
                if (a.data != null) Array.Copy(a.data, data, Math.Min(a.data.Length, data.Length));
                a.data = data;
            }
            return a;
        }
    }

    public sealed class ReferenceCurveData
    {
        public string Name { get; set; }
        public double[] Points { get; set; }
        public double[] PointsY { get; set; }
        public double MaxSpeed { get; set; }
        public DateTime PinnedAt { get; set; }

        public JObject ToDto() => new JObject
        {
            ["name"] = Name,
            ["x"] = Engine.Arr(Points),
            ["y"] = PointsY != null ? Engine.Arr(PointsY) : null,
            ["maxSpeed"] = MaxSpeed,
            ["pinnedAt"] = PinnedAt.ToString("HH:mm:ss")
        };
    }

    public sealed class SpeedHistogram
    {
        const int BinCount = 100;
        readonly int[] bins = new int[BinCount];
        readonly object sync = new object();
        long totalCount;
        double maxObservedSpeed;

        public void Record(double speed, double maxSpeed)
        {
            if (speed <= 0 || maxSpeed <= 0) return;
            lock (sync)
            {
                totalCount++;
                if (speed > maxObservedSpeed) maxObservedSpeed = speed;
                int idx = (int)Math.Floor((speed / maxSpeed) * BinCount);
                if (idx < 0) idx = 0;
                if (idx >= BinCount) idx = BinCount - 1;
                bins[idx]++;
            }
        }

        public void Reset()
        {
            lock (sync)
            {
                Array.Clear(bins, 0, BinCount);
                totalCount = 0;
                maxObservedSpeed = 0;
            }
        }

        public JObject ToDto(double maxSpeed)
        {
            lock (sync)
            {
                var binArray = new JArray();
                double binWidth = maxSpeed > 0 ? maxSpeed / BinCount : 1.0;
                double median = 0, p90 = 0, p99 = 0;
                long running = 0;
                long target50 = totalCount / 2;
                long target90 = (long)(totalCount * 0.90);
                long target99 = (long)(totalCount * 0.99);

                for (int i = 0; i < BinCount; i++)
                {
                    double speed = Math.Round((i + 0.5) * binWidth, 3);
                    int count = bins[i];
                    running += count;
                    if (median == 0 && running >= target50 && totalCount > 0) median = speed;
                    if (p90 == 0 && running >= target90 && totalCount > 0) p90 = speed;
                    if (p99 == 0 && running >= target99 && totalCount > 0) p99 = speed;

                    binArray.Add(new JObject
                    {
                        ["speed"] = speed,
                        ["count"] = count,
                        ["percent"] = totalCount > 0 ? Math.Round((double)count / totalCount * 100, 2) : 0
                    });
                }

                return new JObject
                {
                    ["totalSamples"] = totalCount,
                    ["peakSpeed"] = Math.Round(maxObservedSpeed, 3),
                    ["medianSpeed"] = Math.Round(median, 3),
                    ["p90Speed"] = Math.Round(p90, 3),
                    ["p99Speed"] = Math.Round(p99, 3),
                    ["maxSpeed"] = maxSpeed,
                    ["bins"] = binArray
                };
            }
        }
    }
}
