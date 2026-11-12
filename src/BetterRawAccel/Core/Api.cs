using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BetterRawAccel.Core
{
    public sealed class ToolDef
    {
        public string Name;
        public string Description;
        public JObject Schema;
        /// <summary>Changes something the window shows, so the UI must refresh after it.</summary>
        public bool Mutates;
        /// <summary>Only for the app's own window, not offered over MCP.</summary>
        public bool UiOnly;
        public Func<JObject, Task<JToken>> Handler;
    }

    /// <summary>What the API needs from the window that hosts it.</summary>
    public interface IHost
    {
        Task<string> CaptureWindowPngBase64();
        JObject LiveInput();
        JObject McpInfo();
        void AppSettingsChanged(ICollection<string> keys);
        string ExportToFile(string json);
        string ImportFromFile();
        void OpenUrl(string url);
        void ShowPanel(string panel);
        void ToggleOverlay(bool? visible = null, bool? movable = null);
        JObject OverlayState();
    }

    /// <summary>
    /// One list of operations used by both the window and the MCP server,
    /// so anything the UI can do an MCP client can do, and the other way round.
    /// </summary>
    public sealed class Api
    {
        static readonly string[] Themes = {
            "paper", "ledger", "parchment", "millimeter", "technical", "chalk",
            "blueprint", "graphite", "phosphor", "obsidian", "midnight", "radar", "cyberpunk", "solarized"
        };
        static readonly string[] Fonts = { "satoshi", "cabinet", "archivo" };

        readonly Engine engine;
        readonly Updater updater;
        readonly IHost host;
        readonly Dictionary<string, ToolDef> tools = new Dictionary<string, ToolDef>();

        public Api(Engine engine, Updater updater, IHost host)
        {
            this.engine = engine;
            this.updater = updater;
            this.host = host;
            Register();
        }

        public IEnumerable<ToolDef> Tools => tools.Values;
        public ToolDef Find(string name) => tools.TryGetValue(name ?? "", out var t) ? t : null;

        public Task<JToken> Call(string name, JObject args)
        {
            var t = Find(name) ?? throw new InvalidOperationException("Unknown tool: " + name);
            return t.Handler(args ?? new JObject());
        }

        static JObject S(string json) => JObject.Parse(json);
        static readonly JObject NoArgs = S("{'type':'object','properties':{},'additionalProperties':false}");

        void Add(string name, string desc, JObject schema, bool mutates, Func<JObject, Task<JToken>> h, bool uiOnly = false)
        {
            tools[name] = new ToolDef { Name = name, Description = desc, Schema = schema ?? NoArgs, Mutates = mutates, Handler = h, UiOnly = uiOnly };
        }

        void Add(string name, string desc, JObject schema, bool mutates, Func<JObject, JToken> h, bool uiOnly = false) =>
            Add(name, desc, schema, mutates, a => Task.FromResult(h(a)), uiOnly);

        static string Req(JObject a, string key)
        {
            var v = (string)a[key];
            if (string.IsNullOrWhiteSpace(v)) throw new InvalidOperationException($"\"{key}\" is required");
            return v;
        }

        Profile Source(JObject a)
        {
            var src = ((string)a["source"] ?? "editing").ToLowerInvariant();
            if (src == "driver")
                return engine.DriverTwin() ?? throw new InvalidOperationException("The driver has no profile with this name yet. Apply first.");
            if (src != "editing") throw new InvalidOperationException("source must be \"editing\" or \"driver\"");
            return engine.Current;
        }

        void Register()
        {
            const string profileProps =
                "'name':{'type':'string'}," +
                "'whole':{'type':'boolean','description':'true = one curve for combined movement; false = separate curves for X and Y (uses x and y)'}," +
                "'sens':{'type':'number','description':'sensitivity multiplier, 1 = unchanged'}," +
                "'yxRatio':{'type':'number'},'lrRatio':{'type':'number'},'udRatio':{'type':'number'}," +
                "'rotation':{'type':'number','description':'degrees'},'snap':{'type':'number','description':'angle snapping, 0-45 degrees'}," +
                "'speedCap':{'type':'number','description':'input speed cap in counts/ms, 0 = none'}," +
                "'lpNorm':{'type':'number'},'inputSmooth':{'type':'number'},'scaleSmooth':{'type':'number'},'outputSmooth':{'type':'number'}," +
                "'domainX':{'type':'number'},'domainY':{'type':'number'},'rangeX':{'type':'number'},'rangeY':{'type':'number'},";
            const string argsSchema =
                "{'type':'object','description':'Acceleration arguments. Only the fields for the chosen mode matter.','properties':{" +
                "'mode':{'type':'string','enum':['off','linear','classic','natural','power','synchronous','jump','lut']}," +
                "'gain':{'type':'boolean','description':'true = curve is applied to gain, false = to velocity (legacy). In lut mode it means something else: true = point values are output speeds, false = point values are sensitivities'}," +
                "'acceleration':{'type':'number'},'exponentClassic':{'type':'number'},'inputOffset':{'type':'number'},'outputOffset':{'type':'number'}," +
                "'decayRate':{'type':'number'},'limit':{'type':'number'},'scale':{'type':'number'},'exponentPower':{'type':'number'}," +
                "'syncSpeed':{'type':'number'},'motivity':{'type':'number'},'gamma':{'type':'number'},'smooth':{'type':'number'}," +
                "'capX':{'type':'number','description':'cap or jump input speed, counts/ms'},'capY':{'type':'number','description':'cap or jump output multiplier'}," +
                "'capMode':{'type':'string','enum':['output','input','in_out']}," +
                "'lut':{'type':'array','description':'[[speed, value], ...] with increasing speeds','items':{'type':'array','items':{'type':'number'}}}}}";

            Add("get_state",
                "Summary of the app: selected profile and its values, profile names, whether there are changes not yet written to the driver, and validation errors.",
                null, false, a => engine.Summary());

            Add("list_modes",
                "Acceleration modes with their parameters, ranges and defaults. Read this before changing parameters.",
                null, false, a => engine.Meta);

            Add("get_profile", "All values of one profile (default: the selected one).",
                S("{'type':'object','properties':{'profile':{'type':['string','integer'],'description':'name or index'}}}"),
                false, a => Engine.ToDto(engine.Edit.profiles[engine.FindProfile(a["profile"])]));

            Add("update_profile",
                "Change values of the selected profile. The window updates live. Nothing reaches the driver until apply is called. Invalid combinations are rejected and nothing changes.",
                S("{'type':'object','required':['patch'],'properties':{'patch':{'type':'object','properties':{" + profileProps +
                  "'x':" + argsSchema + ",'y':" + argsSchema + "}}}}"),
                true, a =>
                {
                    var patch = a["patch"] as JObject ?? throw new InvalidOperationException("\"patch\" must be an object");
                    engine.UpdateProfile(patch);
                    return engine.Summary();
                });

            Add("set_mode", "Switch the acceleration mode of the selected profile. Each mode keeps its own parameter values.",
                S("{'type':'object','required':['mode'],'properties':{'mode':{'type':'string','enum':['off','linear','classic','natural','power','synchronous','jump','lut']},'axis':{'type':'string','enum':['x','y'],'description':'y only matters when whole is false'}}}"),
                true, a =>
                {
                    var axis = ((string)a["axis"] ?? "x").ToLowerInvariant();
                    if (axis != "x" && axis != "y") throw new InvalidOperationException("axis must be x or y");
                    engine.UpdateProfile(new JObject { [axis] = new JObject { ["mode"] = Req(a, "mode") } });
                    return engine.Summary();
                });

            Add("get_curve",
                "Sensitivity (output speed / input speed) sampled at evenly spaced input speeds from 0 to max_speed counts/ms, computed with the driver's own math.",
                S("{'type':'object','properties':{'source':{'type':'string','enum':['editing','driver'],'description':'editing = what the window shows, driver = what is active now'},'points':{'type':'integer','minimum':2,'maximum':1000},'max_speed':{'type':'number','exclusiveMinimum':0}}}"),
                false, a =>
                {
                    int n = (int?)a["points"] ?? 33;
                    double max = (double?)a["max_speed"] ?? engine.MaxSpeed;
                    if (n < 2 || n > 1000) throw new InvalidOperationException("points must be between 2 and 1000");
                    if (!(max > 0) || max > 100000) throw new InvalidOperationException("max_speed must be greater than 0");
                    var c = engine.Curve(Source(a), n, max) ?? throw new InvalidOperationException("The profile is not valid: " + Engine.Validate(Source(a)));
                    c["maxSpeed"] = max;
                    c["points"] = n;
                    c["note"] = "x[i] is sensitivity at speed i*maxSpeed/(points-1); y is null unless X and Y are separate";
                    return c;
                });

            Add("evaluate", "Sensitivity at one input speed (counts/ms).",
                S("{'type':'object','required':['speed'],'properties':{'speed':{'type':'number','exclusiveMinimum':0},'axis':{'type':'string','enum':['x','y']},'source':{'type':'string','enum':['editing','driver']}}}"),
                false, a =>
                {
                    double speed = (double?)a["speed"] ?? throw new InvalidOperationException("\"speed\" is required");
                    double s = engine.Evaluate(Source(a), speed, ((string)a["axis"] ?? "x").ToLowerInvariant() == "y");
                    return new JObject { ["speed"] = speed, ["sensitivity"] = Math.Round(s, 5), ["outputSpeed"] = Math.Round(s * speed, 5) };
                });

            Add("apply",
                "Write the edited settings to the Raw Accel driver and save them to settings.json. Takes about one second because the driver delays every write.",
                null, true, async a =>
                {
                    await engine.ApplyAsync();
                    return engine.Summary();
                });

            Add("revert", "Discard changes that were not written and return to what the driver has.", null, true, a =>
            {
                engine.Revert();
                return engine.Summary();
            });

            Add("reset_driver", "Turn acceleration off in the driver (back to defaults). The edited settings stay in the window as unwritten changes.",
                null, true, async a =>
                {
                    await engine.ResetDriverAsync();
                    return engine.Summary();
                });

            Add("undo", "Undo the last edit.", null, true, a => { engine.Undo(); return engine.Summary(); });
            Add("redo", "Redo the last undone edit.", null, true, a => { engine.Redo(); return engine.Summary(); });

            Add("list_profiles", "Profile names in the editor and in the driver, the one open for editing, and the one your mice use in the driver right now.", null, false, a => new JObject
            {
                ["editing"] = new JArray(engine.Edit.profiles.Select(p => p.name)),
                ["driver"] = new JArray(engine.Active.profiles.Select(p => p.name)),
                ["selected"] = engine.Current.name,
                ["inUse"] = engine.InUse,
            });

            var which = S("{'type':'object','required':['profile'],'properties':{'profile':{'type':['string','integer'],'description':'name or index'}}}");
            Add("use_profile",
                "Make your mouse use this profile and write that to the driver at once (about one second). Mice that were given a profile of their own under Devices switch; if none were, every connected mouse switches. " +
                "If the profile is already in the driver, other unwritten edits stay unwritten. Also opens the profile for editing.",
                which, true, async a =>
                {
                    if (a["profile"] == null) throw new InvalidOperationException("\"profile\" is required");
                    await engine.UseProfileAsync(a["profile"]);
                    return engine.Summary();
                });

            Add("select_profile", "Open a profile for editing. This does not change which profile your mice use; use_profile does that.", which, true, a =>
            {
                if (a["profile"] == null) throw new InvalidOperationException("\"profile\" is required");
                engine.SelectProfile(a["profile"]);
                return engine.Summary();
            });

            Add("create_profile", "Add a profile, optionally copying another one, and select it.",
                S("{'type':'object','required':['name'],'properties':{'name':{'type':'string'},'copy_from':{'type':['string','integer']}}}"),
                true, a =>
                {
                    engine.CreateProfile(Req(a, "name"), a["copy_from"]);
                    return engine.Summary();
                });

            Add("rename_profile", "Rename a profile (default: the selected one). Device assignments follow the new name.",
                S("{'type':'object','required':['name'],'properties':{'profile':{'type':['string','integer']},'name':{'type':'string'}}}"),
                true, a =>
                {
                    engine.RenameProfile(a["profile"], Req(a, "name"));
                    return engine.Summary();
                });

            Add("delete_profile", "Delete a profile. The first profile can not be deleted.", which, true, a =>
            {
                if (a["profile"] == null) throw new InvalidOperationException("\"profile\" is required");
                engine.DeleteProfile(a["profile"]);
                return engine.Summary();
            });

            const string devProps =
                "'dpi':{'type':'integer','description':'0 = do not normalise; otherwise input speed is converted to inches/s using this DPI'}," +
                "'pollingRate':{'type':'integer','description':'Hz, 0 = automatic'}," +
                "'disable':{'type':'boolean','description':'true = Raw Accel leaves this device alone'}," +
                "'pollTimeLock':{'type':'boolean','description':'use a constant time interval based on the polling rate'}";
            Add("list_devices", "Connected mice, saved per-device settings, and the default device settings.", null, false, a => engine.Devices());

            Add("set_device", "Give one device its own settings and/or profile. Find ids with list_devices.",
                S("{'type':'object','required':['id'],'properties':{'id':{'type':'string'},'profile':{'type':'string','description':'profile name, empty for the default profile'}," + devProps + "}}"),
                true, a =>
                {
                    var id = Req(a, "id");
                    var patch = (JObject)a.DeepClone();
                    patch.Remove("id");
                    engine.SetDevice(id, patch);
                    return engine.Devices();
                });

            Add("set_default_device", "Settings used by every device that has no settings of its own.",
                S("{'type':'object','properties':{" + devProps + "}}"), true, a =>
                {
                    engine.SetDefaultDevice(a);
                    return engine.Devices();
                });

            Add("remove_device", "Remove a device's own settings so it uses the defaults again.",
                S("{'type':'object','required':['id'],'properties':{'id':{'type':'string'}}}"), true, a =>
                {
                    engine.RemoveDevice(Req(a, "id"));
                    return engine.Devices();
                });

            Add("get_live_input", "Current hand speed measured from the mouse, the sensitivity at that speed, and the recent peak.", null, false, a => host.LiveInput());

            Add("export_settings", "The edited settings as Raw Accel settings.json text.", null, false, a => new JObject { ["json"] = engine.Export() });

            Add("import_settings", "Replace the edited settings with Raw Accel settings.json text. Nothing reaches the driver until apply is called.",
                S("{'type':'object','required':['json'],'properties':{'json':{'type':'string'}}}"), true, a =>
                {
                    engine.Import(Req(a, "json"));
                    return engine.Summary();
                });

            Add("get_app_settings", "Window preferences: theme, typeface, plot scale, updates, start-up, MCP.", null, false, a => AppSettingsDto());

            Add("set_app_settings", "Change window preferences, including which plot is shown (plotView). Themes: paper, ledger, parchment, millimeter, technical, chalk, blueprint, graphite, phosphor, obsidian, midnight, radar, cyberpunk, solarized. Fonts: satoshi, cabinet, archivo.",
                S("{'type':'object','properties':{'theme':{'type':'string','enum':['paper','ledger','parchment','millimeter','technical','chalk','blueprint','graphite','phosphor','obsidian','midnight','radar','cyberpunk','solarized']},'font':{'type':'string','enum':['satoshi','cabinet','archivo']}," +
                  "'plotDpi':{'type':'integer','description':'mouse DPI; only sets the plot x range (DPI x 0.05 counts/ms)'},'plotPollRate':{'type':'integer'}," +
                  "'autoUpdate':{'type':'boolean'},'applyOnStart':{'type':'boolean','description':'write settings.json to the driver when the app starts'}," +
                  "'startWithWindows':{'type':'boolean','description':'apply settings at logon using the bundled writer.exe'}," +
                  "'overlayEnabled':{'type':'boolean','description':'show transparent in-game HUD overlay for Valorant/games'},'overlayPosition':{'type':'string','enum':['top-right','top-left','bottom-right','top-center']},'overlayAutoFade':{'type':'boolean'},'overlayHotkey':{'type':'string'}," +
                  "'plotView':{'type':'string','enum':['sens','vel','gain'],'description':'what the plot shows: sensitivity, output velocity, or gain'},'mcpEnabled':{'type':'boolean'},'mcpPort':{'type':'integer'},'helpSeen':{'type':'boolean'}}}"),
                true, a =>
                {
                    SetAppSettings(a);
                    return AppSettingsDto();
                });

            Add("check_for_updates", "Look for a newer release on GitHub. Does not install anything.", null, true, async a =>
            {
                await updater.CheckAsync(download: false);
                return updater.Dto();
            });

            Add("install_update", "Download the newest release, verify it, replace the app files and restart the app.", null, true, async a =>
            {
                await updater.CheckAsync(download: true);
                if (updater.State != "ready") return updater.Dto();
                updater.ApplyAndRestartSoon();
                var d = updater.Dto();
                d["note"] = "The app is restarting into the new version.";
                return d;
            });

            Add("capture_window", "A PNG screenshot of the app window as it looks right now.", null, false, async a =>
                new JObject { ["__image"] = await host.CaptureWindowPngBase64(), ["mimeType"] = "image/png" });

            Add("show_panel", "Open one of the window's panels so the person at the PC can see it, or close them with none.",
                S("{'type':'object','required':['panel'],'properties':{'panel':{'type':'string','enum':['profiles','devices','system','help','themes','fonts','cm360','none']}}}"),
                false, a =>
                {
                    var panel = Req(a, "panel").ToLowerInvariant();
                    if (Array.IndexOf(new[] { "profiles", "devices", "system", "help", "themes", "fonts", "cm360", "none" }, panel) < 0)
                        throw new InvalidOperationException("Unknown panel: " + panel);
                    host.ShowPanel(panel);
                    return new JObject { ["panel"] = panel };
                });

            Add("toggle_overlay",
                "Toggle or set the in-game HUD overlay visibility and movable/fixed mode. Shows active profile, mouse device, and sens over games like Valorant.",
                S("{'type':'object','properties':{'visible':{'type':'boolean','description':'true to show, false to hide, or omit to toggle'},'movable':{'type':'boolean','description':'true for movable drag mode, false for fixed click-through mode'}},'additionalProperties':false}"),
                true, a =>
                {
                    bool? vis = a["visible"] != null ? (bool?)a["visible"] : null;
                    bool? mov = a["movable"] != null ? (bool?)a["movable"] : null;
                    host.ToggleOverlay(vis, mov);
                    return host.OverlayState();
                });

            Add("get_overlay_state",
                "Get the in-game HUD overlay status, position, and currently displayed profile and device.",
                null, false, a => host.OverlayState());

            Add("calculate_cm360",
                "Calculate cm/360, in/360, curve range (slowest to fastest cm/360), and equivalent sensitivities across games (CS2/Source/Apex, Valorant, Overwatch 2, Fortnite, Rainbow Six Siege).",
                S("{'type':'object','properties':{'dpi':{'type':'integer','description':'mouse DPI, defaults to app plot DPI or 800'},'game':{'type':'string','enum':['source','cs2','csgo','apex','quake','titanfall','teamfortress2','callofduty','cod','warzone','valorant','overwatch','overwatch2','fortnite','rainbow6','r6','destiny2','custom'],'description':'defaults to source / cs2'},'game_sens':{'type':'number','description':'in-game sensitivity slider value, defaults to 1.0'},'yaw':{'type':'number','description':'custom yaw (degrees/count) when game is custom'},'speed':{'type':'number','description':'input speed in counts/ms to evaluate effective cm/360 at, defaults to 0'}}}"),
                false, a =>
                {
                    double dpi = (double?)a["dpi"] ?? engine.App.PlotDpi;
                    string game = (string)a["game"] ?? "source";
                    double sens = (double?)a["game_sens"] ?? 1.0;
                    double yaw = (double?)a["yaw"] ?? 0;
                    double? speed = (double?)a["speed"];
                    return SensitivityCalculator.Analyze(engine, engine.Current, dpi, game, sens, yaw, speed);
                });

            Add("convert_sensitivity",
                "Convert sensitivity from one game and DPI to another game and DPI.",
                S("{'type':'object','required':['from_game','from_sens'],'properties':{'from_game':{'type':'string'},'from_sens':{'type':'number'},'from_dpi':{'type':'number'},'to_game':{'type':'string'},'to_dpi':{'type':'number'},'custom_from_yaw':{'type':'number'},'custom_to_yaw':{'type':'number'}}}"),
                false, a =>
                {
                    string fromGame = Req(a, "from_game");
                    double fromSens = (double?)a["from_sens"] ?? throw new InvalidOperationException("from_sens required");
                    double fromDpi = (double?)a["from_dpi"] ?? engine.App.PlotDpi;
                    string toGame = (string)a["to_game"] ?? "source";
                    double toDpi = (double?)a["to_dpi"] ?? fromDpi;
                    double fromYaw = (double?)a["custom_from_yaw"] ?? 0;
                    double toYaw = (double?)a["custom_to_yaw"] ?? 0;
                    double converted = SensitivityCalculator.ConvertSens(fromGame, fromSens, fromDpi, toGame, toDpi, fromYaw, toYaw);
                    double cm = SensitivityCalculator.Cm360(toDpi, converted, SensitivityCalculator.GetYaw(toGame, toYaw));
                    return new JObject
                    {
                        ["fromGame"] = fromGame,
                        ["fromSens"] = fromSens,
                        ["fromDpi"] = fromDpi,
                        ["toGame"] = toGame,
                        ["toSens"] = Math.Round(converted, 4),
                        ["toDpi"] = toDpi,
                        ["cm360"] = Math.Round(cm, 2),
                        ["in360"] = Math.Round(cm / 2.54, 2)
                    };
                });

            Add("pin_reference_curve",
                "Pin the current curve or a specific profile as a reference overlay on the plot to compare with active edits.",
                S("{'type':'object','properties':{'source':{'type':'string','description':'editing, driver, or profile name (default: editing)'}}}"),
                true, a =>
                {
                    engine.PinReferenceCurve((string)a["source"]);
                    return engine.PinnedCurve?.ToDto() ?? new JObject();
                });

            Add("clear_reference_curve",
                "Remove the pinned reference curve overlay from the plot.",
                null, true, a =>
                {
                    engine.ClearReferenceCurve();
                    return new JObject { ["cleared"] = true };
                });

            Add("get_reference_curve",
                "Get the pinned reference curve if one is active.",
                null, false, a => engine.PinnedCurve?.ToDto() ?? new JObject { ["pinned"] = false });

            Add("get_speed_histogram",
                "Get the hand speed distribution recorded during mouse movement, including median, 90th percentile, peak speed, and bucket percentages.",
                null, false, a => engine.Histogram.ToDto(engine.MaxSpeed));

            Add("reset_speed_histogram",
                "Clear accumulated hand speed distribution data and restart recording.",
                null, false, a =>
                {
                    engine.Histogram.Reset();
                    return new JObject { ["reset"] = true };
                });

            Add("export_profile_code",
                "Export a profile as a compact shareable short code (e.g. RA1:...) that can be shared or pasted into another Better Raw Accel instance.",
                S("{'type':'object','properties':{'profile':{'type':['string','integer'],'description':'name or index, defaults to current profile'}}}"),
                false, a =>
                {
                    var p = a["profile"] != null ? engine.Edit.profiles[engine.FindProfile(a["profile"])] : engine.Current;
                    string code = ProfileSharing.ExportCode(p);
                    return new JObject { ["name"] = p.name, ["code"] = code };
                });

            Add("import_profile_code",
                "Import and validate a profile from a share code (RA1:...). Adds the profile to your profiles list and selects it.",
                S("{'type':'object','required':['code'],'properties':{'code':{'type':'string'},'name':{'type':'string','description':'optional new name for imported profile'}}}"),
                true, a =>
                {
                    string code = Req(a, "code");
                    var patch = ProfileSharing.ImportCode(code);
                    string name = (string)a["name"] ?? (string)patch["name"] ?? "imported";
                    int count = 1;
                    string targetName = name;
                    while (engine.Edit.profiles.Any(p => string.Equals(p.name, targetName, StringComparison.OrdinalIgnoreCase)))
                    {
                        count++;
                        targetName = $"{name} ({count})";
                    }
                    engine.CreateProfile(targetName, null);
                    patch["name"] = targetName;
                    engine.UpdateProfile(patch);
                    return engine.Summary();
                });

            Add("export_plot_image",
                "Export the current plot window as a PNG base64 image.",
                null, false, async a =>
                {
                    string b64 = await host.CaptureWindowPngBase64();
                    return new JObject { ["__image"] = b64, ["mimeType"] = "image/png" };
                });

            // ---- window-only helpers
            Add("ui_state", "", S("{'type':'object'}"), false, a => FullState(), uiOnly: true);
            Add("ui_export_file", "", null, false, a => new JObject { ["path"] = host.ExportToFile(engine.Export()) }, uiOnly: true);
            Add("ui_import_file", "", null, true, a =>
            {
                var json = host.ImportFromFile();
                if (json == null) return new JObject { ["cancelled"] = true };
                engine.Import(json);
                return new JObject { ["cancelled"] = false };
            }, uiOnly: true);
            Add("ui_open_url", "", S("{'type':'object'}"), false, a =>
            {
                host.OpenUrl(Req(a, "url"));
                return new JObject();
            }, uiOnly: true);
        }

        public JObject FullState()
        {
            var st = engine.UiState(false);
            st["settings"] = AppSettingsDto();
            st["mcp"] = host.McpInfo();
            st["update"] = updater.Dto();
            return st;
        }

        JObject AppSettingsDto()
        {
            var o = engine.SettingsDto();
            o["startWithWindows"] = Startup.IsEnabled();
            return o;
        }

        void SetAppSettings(JObject a)
        {
            var s = engine.App;
            var changed = new List<string>();
            foreach (var kv in a)
            {
                var v = kv.Value;
                switch (kv.Key)
                {
                    case "theme":
                        if (!Themes.Contains((string)v)) throw new InvalidOperationException("Unknown theme: " + v);
                        s.Theme = (string)v; break;
                    case "font":
                        if (!Fonts.Contains((string)v)) throw new InvalidOperationException("Unknown font: " + v);
                        s.Font = (string)v; break;
                    case "plotDpi":
                        if ((int)v < 100 || (int)v > 64000) throw new InvalidOperationException("plotDpi must be between 100 and 64000");
                        s.PlotDpi = (int)v; break;
                    case "plotPollRate":
                        if ((int)v < 125 || (int)v > 8000) throw new InvalidOperationException("plotPollRate must be between 125 and 8000");
                        s.PlotPollRate = (int)v; break;
                    case "autoUpdate": s.AutoUpdate = (bool)v; break;
                    case "applyOnStart": s.ApplyOnStart = (bool)v; break;
                    case "helpSeen": s.HelpSeen = (bool)v; break;
                    case "plotView":
                        if ((string)v != "sens" && (string)v != "vel" && (string)v != "gain") throw new InvalidOperationException("plotView must be sens, vel or gain");
                        s.PlotView = (string)v; break;
                    case "mcpEnabled": s.McpEnabled = (bool)v; break;
                    case "mcpPort":
                        if ((int)v < 1024 || (int)v > 65535) throw new InvalidOperationException("mcpPort must be between 1024 and 65535");
                        s.McpPort = (int)v; break;
                    case "startWithWindows": Startup.Set((bool)v); break;
                    case "overlayEnabled": s.OverlayEnabled = (bool)v; break;
                    case "overlayPosition": s.OverlayPosition = (string)v; break;
                    case "overlayAutoFade": s.OverlayAutoFade = (bool)v; break;
                    case "overlayHotkey": s.OverlayHotkey = (string)v; break;
                    case "overlayMovable": s.OverlayMovable = (bool)v; break;
                    case "overlayCustomX": s.OverlayCustomX = Convert.ToInt32(v); break;
                    case "overlayCustomY": s.OverlayCustomY = Convert.ToInt32(v); break;
                    default: throw new InvalidOperationException("Unknown setting: " + kv.Key);
                }
                changed.Add(kv.Key);
            }
            s.Save();
            host.AppSettingsChanged(changed);
        }
    }

    /// <summary>"Start with Windows": a Startup-folder shortcut that runs Raw Accel's writer.exe on settings.json at logon.</summary>
    public static class Startup
    {
        static string LinkPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup), "BetterRawAccel settings.lnk");

        public static bool IsEnabled()
        {
            try { return System.IO.File.Exists(LinkPath); }
            catch { return false; }
        }

        public static void Set(bool on)
        {
            if (!on)
            {
                if (System.IO.File.Exists(LinkPath)) System.IO.File.Delete(LinkPath);
                return;
            }
            var writer = System.IO.Path.Combine(Paths.AppDir, "writer.exe");
            if (!System.IO.File.Exists(writer)) throw new InvalidOperationException("writer.exe is missing from the app folder");
            if (!System.IO.File.Exists(Paths.SettingsFile)) throw new InvalidOperationException("Write to the driver once first, so there are settings to apply at logon");

            var t = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is not available");
            dynamic shell = Activator.CreateInstance(t);
            try
            {
                dynamic lnk = shell.CreateShortcut(LinkPath);
                try
                {
                    lnk.TargetPath = writer;
                    lnk.Arguments = "\"" + Paths.SettingsFile + "\"";
                    lnk.WorkingDirectory = Paths.AppDir;
                    lnk.WindowStyle = 7;
                    lnk.Description = "Apply Raw Accel settings at logon";
                    lnk.Save();
                }
                finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(lnk); }
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
    }
}
