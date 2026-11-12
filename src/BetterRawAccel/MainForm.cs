using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using BetterRawAccel.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BetterRawAccel
{
    public sealed class MainForm : Form, IHost
    {
        const string UiHost = "app.rawaccel";

        readonly AppSettings app;
        readonly Engine engine;
        readonly Updater updater = new Updater();
        readonly Api api;
        readonly McpServer mcp;
        readonly OverlayForm overlay;
        readonly GameWatch gameWatch = new GameWatch();
        readonly Timer gameTimer = new Timer { Interval = 2000 };
        NotifyIcon tray;
        bool quitting;
        string hotkeyProblem;
        readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill };
        readonly Timer inputTimer = new Timer { Interval = 16 };
        readonly Stopwatch inputClock = Stopwatch.StartNew();

        bool webReady;
        bool restartAfterUpdate;
        double accDistance;
        Dictionary<IntPtr, bool> liveHandles = new Dictionary<IntPtr, bool>();
        bool liveDirty = true;
        double lastOut, lastIn, peakIn;
        long peakAt;
        bool lastSentZero = true;

        public MainForm(AppSettings app, Engine engine)
        {
            this.app = app;
            this.engine = engine;
            overlay = new OverlayForm(app);
            api = new Api(engine, updater, this);
            mcp = new McpServer(() => api.Tools, InvokeFromMcp);

            Text = AppInfo.Name;
            Icon = Icon.ExtractAssociatedIcon(Paths.ExePath);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = DeskColor(app.Theme);
            MinimumSize = Scale(new Size(820, 640));
            ApplySavedBounds();
            Controls.Add(web);
            web.DefaultBackgroundColor = BackColor;

            updater.Changed += () => Post(new JObject { ["event"] = "update", ["update"] = updater.Dto() });
            updater.RestartRequested += () => BeginInvoke((Action)(() => { restartAfterUpdate = true; Close(); }));
            inputTimer.Tick += (s, e) => FlushInput();

            engine.Changed += () =>
            {
                if (InvokeRequired) BeginInvoke((Action)UpdateOverlay);
                else UpdateOverlay();
            };

            overlay.PositionSaved += pt =>
            {
                app.OverlayPosition = "custom";
                app.OverlayCustomX = pt.X;
                app.OverlayCustomY = pt.Y;
                app.Save();
                Post(new JObject { ["event"] = "overlay_settings", ["settings"] = OverlayState() });
            };

            overlay.MovableStateChanged += mov =>
            {
                app.OverlayMovable = mov;
                app.Save();
                Post(new JObject { ["event"] = "overlay_settings", ["settings"] = OverlayState() });
            };

            Activated += (s, e) =>
            {
                SetRawInputActive(true);
                Post(new JObject { ["event"] = "window_focus", ["active"] = true });
                try { web.CoreWebView2?.Resume(); } catch { }
            };

            Deactivate += (s, e) =>
            {
                SetRawInputActive(false);
                Post(new JObject { ["event"] = "window_focus", ["active"] = false });
            };

            Resize += (s, e) =>
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    SetRawInputActive(false);
                    Post(new JObject { ["event"] = "window_focus", ["active"] = false });
                    try { web.CoreWebView2?.TrySuspendAsync(); } catch { }
                }
                else
                {
                    try { web.CoreWebView2?.Resume(); } catch { }
                }
            };

            Load += async (s, e) => await Start();
            FormClosing += (s, e) =>
            {
                // With "keep running in the tray", the close button hides the window instead of quitting,
                // so per-game profiles and hotkeys keep working.
                if (app.CloseToTray && !quitting && !restartAfterUpdate && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    SaveBounds();
                    Hide();
                    ShowTrayTip();
                    return;
                }
                SaveBounds();
            };
            gameTimer.Tick += (s, e) => CheckGames();
            FormClosed += (s, e) =>
            {
                mcp.Dispose();
                gameTimer.Stop();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                UnregisterGlobalHotkeys();
                overlay?.Dispose();
                if (updater.Ready) updater.LaunchSwap(restartAfterUpdate);
            };
        }

        // ------------------------------------------------------------ window

        Size Scale(Size s)
        {
            float f;
            using (var g = CreateGraphics()) f = g.DpiX / 96f;
            return new Size((int)(s.Width * f), (int)(s.Height * f));
        }

        static Color DeskColor(string theme)
        {
            switch (theme)
            {
                case "ledger": return ColorTranslator.FromHtml("#C7D1BC");
                case "parchment": return ColorTranslator.FromHtml("#D4C7B0");
                case "millimeter": return ColorTranslator.FromHtml("#DDD8CD");
                case "technical": return ColorTranslator.FromHtml("#C8D1D9");
                case "chalk": return ColorTranslator.FromHtml("#CED5D8");
                case "blueprint": return ColorTranslator.FromHtml("#0A2140");
                case "graphite": return ColorTranslator.FromHtml("#0D0D0C");
                case "phosphor": return ColorTranslator.FromHtml("#070604");
                case "obsidian": return ColorTranslator.FromHtml("#000000");
                case "midnight": return ColorTranslator.FromHtml("#060A12");
                case "radar": return ColorTranslator.FromHtml("#020A04");
                case "cyberpunk": return ColorTranslator.FromHtml("#0D0A14");
                case "solarized": return ColorTranslator.FromHtml("#00191F");
                default: return ColorTranslator.FromHtml("#D9D5C9");
            }
        }

        void ApplySavedBounds()
        {
            var w = app.Window;
            if (w != null && w.Length == 4 && w[2] >= 400 && w[3] >= 300)
            {
                var r = new Rectangle(w[0], w[1], w[2], w[3]);
                // Only reuse the saved place if a good part of it is still on a connected monitor.
                bool visible = Screen.AllScreens.Any(s =>
                {
                    var i = Rectangle.Intersect(s.WorkingArea, r);
                    return i.Width >= 200 && i.Height >= 120;
                });
                if (visible)
                {
                    StartPosition = FormStartPosition.Manual;
                    Bounds = r;
                    if (app.Maximized) WindowState = FormWindowState.Maximized;
                    return;
                }
            }
            StartPosition = FormStartPosition.CenterScreen;
            var area = Screen.PrimaryScreen.WorkingArea;
            var want = Scale(new Size(1180, 760));
            Size = new Size(Math.Min(want.Width, area.Width), Math.Min(want.Height, area.Height));
        }

        void SaveBounds()
        {
            if (WindowState == FormWindowState.Minimized && app.Window != null) { app.Save(); return; }
            var r = WindowState == FormWindowState.Normal ? Bounds : base.RestoreBounds;
            app.Window = new[] { r.X, r.Y, r.Width, r.Height };
            app.Maximized = WindowState == FormWindowState.Maximized;
            app.Save();
        }

        // ------------------------------------------------------------ start-up

        async Task Start()
        {
            try
            {
                Directory.CreateDirectory(Paths.WebViewDir);
                var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebViewDir);
                await web.EnsureCoreWebView2Async(env);
            }
            catch (Exception e)
            {
                Log.Error("webview2", e);
                MessageBox.Show(this,
                    "The Microsoft Edge WebView2 Runtime is needed to show the interface and could not be started.\n\n" +
                    "Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and start the app again.\n\n" + e.Message,
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }

            var core = web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(UiHost, Paths.UiDir, CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.AreDefaultContextMenusEnabled = AppInfo.IsDevBuild;
            core.Settings.AreDevToolsEnabled = AppInfo.IsDevBuild;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = AppInfo.IsDevBuild;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.WebMessageReceived += OnWebMessage;
            core.NewWindowRequested += (s, e) => { e.Handled = true; OpenUrl(e.Uri); };
            core.NavigationStarting += (s, e) =>
            {
                // The window only ever shows the bundled interface.
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) || u.Host != UiHost)
                {
                    e.Cancel = true;
                    OpenUrl(e.Uri);
                }
            };
            core.Navigate($"https://{UiHost}/index.html");
            webReady = true;

            SetRawInputActive(true);
            RegisterGlobalHotkeys();
            UpdateTray();
            gameTimer.Start();
            CheckGames();
            UpdateOverlay();
            if (app.OverlayEnabled) overlay.SetVisibility(true);
            if (app.McpEnabled) mcp.Start(app.McpPort);

            if (app.ApplyOnStart && engine.Pending)
            {
                try
                {
                    await engine.ApplyAsync();
                    PushState("startup", "apply");
                }
                catch (Exception e)
                {
                    Log.Error("apply on start", e);
                }
            }

            if (app.AutoUpdate && !AppInfo.IsDevBuild) _ = updater.CheckAsync(download: true);
        }

        // ------------------------------------------------------------ bridge to the interface

        void Post(JObject msg)
        {
            if (!webReady || IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action)(() => Post(msg))); return; }
            try { web.CoreWebView2.PostWebMessageAsJson(msg.ToString(Formatting.None)); }
            catch (Exception e) { Log.Error("post", e); }
        }

        void PushState(string source, string tool)
        {
            liveDirty = true;
            UpdateOverlay();
            Post(new JObject { ["event"] = "state", ["state"] = api.FullState(), ["source"] = source, ["tool"] = tool });
        }

        static string Friendly(Exception e)
        {
            while (e is AggregateException a && a.InnerException != null) e = a.InnerException;
            return e.Message.Trim();
        }

        async void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject msg;
            try { msg = JObject.Parse(e.WebMessageAsJson); }
            catch { return; }

            var reply = new JObject { ["id"] = msg["id"] };
            var method = (string)msg["method"];
            try
            {
                var tool = api.Find(method) ?? throw new InvalidOperationException("Unknown method: " + method);
                var args = msg["params"] as JObject ?? new JObject();
                reply["result"] = await api.Call(method, args);
                if (tool.Mutates)
                {
                    liveDirty = true;
                    UpdateOverlay();
                    bool lite = (method == "update_profile" && args["patch"]?["name"] == null) || method == "set_mode";
                    reply["state"] = lite ? engine.UiState(true) : api.FullState();
                }
            }
            catch (Exception ex)
            {
                if (!(ex is InvalidOperationException)) Log.Error("ui " + method, ex);
                reply["error"] = Friendly(ex);
                try { reply["state"] = api.FullState(); } catch (Exception ex2) { Log.Error("state", ex2); }
            }
            Post(reply);
        }

        /// <summary>Called on an MCP worker thread: run the tool on the UI thread and show the result in the window.</summary>
        JToken InvokeFromMcp(string name, JObject args)
        {
            var tcs = new TaskCompletionSource<JToken>();
            BeginInvoke((Action)(async () =>
            {
                var tool = api.Find(name);
                try
                {
                    Post(new JObject { ["event"] = "mcp", ["tool"] = name, ["phase"] = "start" });
                    var result = await api.Call(name, args);
                    if (tool != null && tool.Mutates) PushState("mcp", name);
                    else Post(new JObject { ["event"] = "mcp", ["tool"] = name, ["phase"] = "done", ["mcp"] = McpInfo() });
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    if (!(ex is InvalidOperationException)) Log.Error("mcp " + name, ex);
                    try { PushState("mcp", name); } catch { }
                    tcs.SetException(new InvalidOperationException(Friendly(ex)));
                }
            }));
            if (!tcs.Task.Wait(TimeSpan.FromSeconds(120))) throw new TimeoutException("The app did not answer in time");
            return tcs.Task.Result;
        }

        // ------------------------------------------------------------ IHost

        public async Task<string> CaptureWindowPngBase64()
        {
            if (!webReady) throw new InvalidOperationException("The window is not ready yet");
            using (var ms = new MemoryStream())
            {
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        /// <summary>The overlay drawn into a bitmap (screen grabs leave out layered windows).</summary>
        public string CaptureOverlayPngBase64()
        {
            var b64 = (string)Invoke((Func<string>)(() => overlay.RenderPngBase64()));
            return b64;
        }

        public JObject LiveInput()
        {
            double sens = lastIn > 0 ? lastOut / lastIn : 0;
            return new JObject
            {
                ["inputSpeed"] = Math.Round(lastIn, 3),
                ["outputSpeed"] = Math.Round(lastOut, 3),
                ["sensitivity"] = Math.Round(sens, 4),
                ["peakInputSpeed"] = Math.Round(peakIn, 3),
                ["unit"] = "counts/ms",
                ["note"] = "Measured from raw mouse input in the last few milliseconds; zero when the mouse is still.",
            };
        }

        public JObject McpInfo() => new JObject
        {
            ["hotkeyProblem"] = hotkeyProblem,
            ["enabled"] = app.McpEnabled,
            ["running"] = mcp.Running,
            ["url"] = mcp.Url,
            ["error"] = mcp.Error,
            ["calls"] = mcp.Calls,
            ["lastTool"] = mcp.LastTool,
            ["lastCall"] = mcp.LastCall?.ToString("HH:mm:ss"),
        };

        public void AppSettingsChanged(ICollection<string> keys)
        {
            if (keys.Contains("theme")) { BackColor = DeskColor(app.Theme); web.DefaultBackgroundColor = BackColor; overlay.SetTheme(app.Theme); }
            if (keys.Contains("overlayPosition") || keys.Contains("overlayCustomX") || keys.Contains("overlayCustomY"))
                overlay.Reposition(app.OverlayPosition, app.OverlayCustomX, app.OverlayCustomY);
            if (keys.Contains("overlayAutoFade")) overlay.AutoFade = app.OverlayAutoFade;
            if (keys.Contains("overlayMovable")) overlay.SetMovable(app.OverlayMovable);
            if (keys.Contains("overlayEnabled")) overlay.SetVisibility(app.OverlayEnabled);
            if (keys.Contains("closeToTray")) UpdateTray();
            if (keys.Contains("gameRules")) { gameWatch.Reset(); BeginInvoke((Action)CheckGames); }
            UpdateOverlay();
            if (keys.Contains("mcpEnabled") || keys.Contains("mcpPort"))
            {
                // Restart after the current request has been answered, in case the request came in over MCP.
                BeginInvoke((Action)(async () =>
                {
                    await Task.Delay(250);
                    if (app.McpEnabled) mcp.Start(app.McpPort); else mcp.Stop();
                    PushState("settings", "mcp");
                }));
            }
        }

        public string ExportToFile(string json)
        {
            using (var d = new SaveFileDialog { Filter = "Raw Accel settings (*.json)|*.json", FileName = "settings.json", Title = "Export settings" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return null;
                AtomicFile.WriteAllText(d.FileName, json);
                return d.FileName;
            }
        }

        public string ImportFromFile()
        {
            using (var d = new OpenFileDialog { Filter = "Raw Accel settings (*.json)|*.json|All files (*.*)|*.*", Title = "Import settings" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return null;
                var info = new FileInfo(d.FileName);
                if (info.Length > 4 * 1024 * 1024) throw new InvalidOperationException("That file is too large to be a settings file");
                return File.ReadAllText(d.FileName);
            }
        }

        public void ShowPanel(string panel) => Post(new JObject { ["event"] = "panel", ["panel"] = panel });

        public void ToggleOverlay(bool? visible = null, bool? movable = null)
        {
            if (InvokeRequired) { BeginInvoke((Action)(() => ToggleOverlay(visible, movable))); return; }
            if (movable.HasValue)
            {
                overlay.SetMovable(movable.Value);
                app.OverlayMovable = movable.Value;
                app.Save();
            }
            if (visible.HasValue)
            {
                overlay.SetVisibility(visible.Value);
                app.OverlayEnabled = overlay.IsActive;
                app.Save();
            }
            else if (!movable.HasValue)
            {
                overlay.Toggle();
                app.OverlayEnabled = overlay.IsActive;
                app.Save();
            }
            UpdateOverlay();
            PushState("overlay", "toggle");
        }

        void ToggleMovable()
        {
            if (InvokeRequired) { BeginInvoke((Action)ToggleMovable); return; }
            if (!overlay.Visible) overlay.SetVisibility(true);
            overlay.SetMovable(!overlay.IsMovable);
            app.OverlayMovable = overlay.IsMovable;
            app.Save();
            UpdateOverlay();
            PushState("overlay", "movable");
        }

        public JObject OverlayState() => new JObject
        {
            ["enabled"] = app.OverlayEnabled,
            ["visible"] = overlay.Visible,
            ["position"] = app.OverlayPosition,
            ["movable"] = overlay.IsMovable,
            ["customX"] = overlay.CustomX,
            ["customY"] = overlay.CustomY,
            ["autoFade"] = app.OverlayAutoFade,
            ["hotkey"] = app.OverlayHotkey,
            ["profile"] = overlay.ProfileName,
            ["device"] = overlay.DeviceName,
            ["mode"] = overlay.ModeName,
            ["sens"] = overlay.SensValue,
            ["cm360"] = overlay.Cm360Value
        };

        void CycleProfile()
        {
            if (InvokeRequired) { BeginInvoke((Action)(CycleProfile)); return; }
            int n = engine.Edit.profiles.Count;
            if (n <= 1) return;
            int cur = engine.Edit.profiles.FindIndex(p => p.name == engine.InUse);
            UseProfileFromHotkey((cur + 1) % n);
        }

        void SwitchProfileByIndex(int idx)
        {
            if (InvokeRequired) { BeginInvoke((Action)(() => SwitchProfileByIndex(idx))); return; }
            if (idx >= 0 && idx < engine.Edit.profiles.Count && engine.Edit.profiles[idx].name != engine.InUse)
                UseProfileFromHotkey(idx);
        }

        void UseProfileFromHotkey(int idx) => UseProfileFrom(idx, "hotkey", null);

        /// <summary>Switch the profile your mice use, in the driver, from a hotkey, the tray menu or a game rule.</summary>
        async void UseProfileFrom(int idx, string source, string reason)
        {
            if (engine.Writing) return;   // the previous switch is still inside the driver's one-second delay
            string error = null;
            try
            {
                await engine.UseProfileAsync(idx);
            }
            catch (Exception ex)
            {
                if (!(ex is InvalidOperationException)) Log.Error("hotkey profile", ex);
                error = Friendly(ex);
            }
            UpdateOverlay();
            overlay.TriggerToast(3.5);
            Post(new JObject { ["event"] = "state", ["state"] = api.FullState(), ["source"] = source, ["tool"] = "use_profile", ["error"] = error, ["reason"] = reason });
        }

        void UpdateOverlay()
        {
            if (InvokeRequired) { BeginInvoke((Action)UpdateOverlay); return; }
            try
            {
                string prof = engine.InUse;
                var cur = engine.Active.profiles.FirstOrDefault(p => p.name == prof) ?? engine.Current;
                string devName = engine.InUseMouse();

                double mult = cur != null && cur.outputDPI > 0 ? cur.outputDPI / 1000.0 : 1.0;
                string sensStr = mult.ToString("0.00") + "\u00d7";

                // cm/360 needs your in-game sensitivity; without it any number would be made up.
                string cmStr = null, gameLabel = null;
                if (app.GameSens > 0 && SensitivityCalculator.IsKnownGame(app.Game))
                {
                    double cm = SensitivityCalculator.Cm360(app.PlotDpi, app.GameSens * mult, SensitivityCalculator.GetYaw(app.Game));
                    cmStr = cm >= 100 ? Math.Round(cm) + " cm" : cm.ToString("0.0") + " cm";
                    gameLabel = SensitivityCalculator.DisplayName(app.Game);
                }

                overlay.UpdateInfo(prof, devName, ModeLabel(cur), sensStr, cmStr, gameLabel);
            }
            catch (Exception ex)
            {
                Log.Error("update overlay", ex);
            }
        }

        /// <summary>The mode as the app's mode tiles name it.</summary>
        static string ModeLabel(Profile p)
        {
            if (p == null) return "Off";
            var a = p.argsX;
            switch (a.mode)
            {
                case AccelMode.noaccel: return "Off";
                case AccelMode.classic: return a.exponentClassic == 2 ? "Linear" : "Classic";
                case AccelMode.natural: return "Natural";
                case AccelMode.power: return "Power";
                case AccelMode.synchronous: return "Sync";
                case AccelMode.jump: return "Jump";
                case AccelMode.lut: return "Table";
                default: return a.mode.ToString();
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        const int WM_HOTKEY = 0x0312;
        const uint MOD_ALT = 0x0001;
        const uint MOD_CONTROL = 0x0002;
        const uint MOD_NOREPEAT = 0x4000;

        const int HOTKEY_ID_OVERLAY = 9001;
        const int HOTKEY_ID_CYCLE = 9002;
        const int HOTKEY_ID_MOVABLE = 9003;

        void RegisterGlobalHotkeys()
        {
            var failed = new List<string>();
            void Reg(int id, Keys key, string label)
            {
                if (!RegisterHotKey(Handle, id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)key)) failed.Add(label);
            }
            try
            {
                Reg(HOTKEY_ID_OVERLAY, Keys.O, "Ctrl+Alt+O");
                Reg(HOTKEY_ID_CYCLE, Keys.P, "Ctrl+Alt+P");
                Reg(HOTKEY_ID_MOVABLE, Keys.M, "Ctrl+Alt+M");
                for (int i = 0; i < 5; i++) Reg(9011 + i, Keys.D1 + i, "Ctrl+Alt+" + (i + 1));
            }
            catch (Exception ex)
            {
                Log.Error("register hotkeys", ex);
            }
            // Another program already owns these keys; say so instead of failing silently.
            hotkeyProblem = failed.Count == 0 ? null : string.Join(", ", failed) + " are taken by another program, so they do nothing here";
            if (hotkeyProblem != null) Log.Write("hotkeys: " + hotkeyProblem);
        }

        // ------------------------------------------------------------ per-game profiles

        string runningGame;

        public string RunningGame() => runningGame;

        void CheckGames()
        {
            try
            {
                var hit = GameWatch.Running(app.GameRules, GameWatch.IsRunning);
                runningGame = hit == null ? null : GameWatch.CleanProcessName(hit.Process);
                if (engine.Writing) return;   // try again on the next tick
                var want = gameWatch.Next(app.GameRules, app.GameRuleFallback, GameWatch.IsRunning);
                if (want == null || want == engine.InUse) return;
                int idx = engine.Edit.profiles.FindIndex(p => p.name == want);
                if (idx < 0) return;
                UseProfileFrom(idx, "game", runningGame != null ? runningGame + " is running" : "No listed game is running");
            }
            catch (Exception e)
            {
                Log.Error("game rules", e);
            }
        }

        // ------------------------------------------------------------ tray

        void UpdateTray()
        {
            if (!app.CloseToTray)
            {
                if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
                return;
            }
            if (tray == null)
            {
                tray = new NotifyIcon { Icon = Icon, Text = AppInfo.Name, Visible = true };
                tray.DoubleClick += (s, e) => ShowFromTray();
                tray.ContextMenuStrip = new ContextMenuStrip();
                tray.ContextMenuStrip.Opening += (s, e) => BuildTrayMenu();
            }
            tray.Visible = true;
        }

        void BuildTrayMenu()
        {
            var m = tray.ContextMenuStrip;
            m.Items.Clear();
            m.Items.Add("Open " + AppInfo.Name, null, (s, e) => ShowFromTray()).Font = new Font(m.Font, FontStyle.Bold);
            m.Items.Add(new ToolStripSeparator());
            string inUse = engine.InUse;
            for (int i = 0; i < engine.Edit.profiles.Count; i++)
            {
                int idx = i;
                var name = engine.Edit.profiles[i].name;
                var item = new ToolStripMenuItem(name, null, (s, e) => UseProfileFrom(idx, "tray", null)) { Checked = name == inUse };
                m.Items.Add(item);
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Quit", null, (s, e) => { quitting = true; Close(); });
        }

        void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        bool trayTipShown;
        void ShowTrayTip()
        {
            if (tray == null || trayTipShown) return;
            trayTipShown = true;
            tray.ShowBalloonTip(4000, AppInfo.Name, "Still running here, so per-game profiles and hotkeys keep working. Right-click to quit.", ToolTipIcon.Info);
        }

        void UnregisterGlobalHotkeys()
        {
            try
            {
                UnregisterHotKey(Handle, HOTKEY_ID_OVERLAY);
                UnregisterHotKey(Handle, HOTKEY_ID_CYCLE);
                UnregisterHotKey(Handle, HOTKEY_ID_MOVABLE);
                for (int i = 0; i < 5; i++) UnregisterHotKey(Handle, 9011 + i);
            }
            catch { }
        }

        public void OpenUrl(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            {
                try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception e) { Log.Error("open url", e); }
            }
        }

        // ------------------------------------------------------------ live mouse input

        const int WM_INPUT = 0x00FF;
        const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
        const uint RID_INPUT = 0x10000003;
        const uint RIM_TYPEMOUSE = 0;
        const uint RIDEV_REMOVE = 0x00000001;
        const uint RIDEV_DEVNOTIFY = 0x00002000;
        const ushort MOUSE_MOVE_ABSOLUTE = 1;

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE { public ushort UsagePage; public ushort Usage; public uint Flags; public IntPtr Target; }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTHEADER { public uint Type; public uint Size; public IntPtr Device; public IntPtr WParam; }

        [StructLayout(LayoutKind.Explicit)]
        struct RAWMOUSE
        {
            [FieldOffset(0)] public ushort Flags;
            [FieldOffset(4)] public uint Buttons;
            [FieldOffset(8)] public uint RawButtons;
            [FieldOffset(12)] public int LastX;
            [FieldOffset(16)] public int LastY;
            [FieldOffset(20)] public uint ExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUT { public RAWINPUTHEADER Header; public RAWMOUSE Mouse; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint count, uint size);

        [DllImport("user32.dll")]
        static extern uint GetRawInputData(IntPtr rawInput, uint command, out RAWINPUT data, ref uint size, uint headerSize);

        bool rawInputActive = false;

        void SetRawInputActive(bool active)
        {
            if (active == rawInputActive) return;
            rawInputActive = active;
            try
            {
                if (active)
                {
                    // Register foreground-only raw input (NO RIDEV_INPUTSINK).
                    // This guarantees zero background interception and zero mouse polling lag during games.
                    var dev = new[] { new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = RIDEV_DEVNOTIFY, Target = Handle } };
                    if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
                        Log.Write("raw input registration failed: " + Marshal.GetLastWin32Error());
                    inputTimer.Start();
                }
                else
                {
                    inputTimer.Stop();
                    var dev = new[] { new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = RIDEV_REMOVE, Target = IntPtr.Zero } };
                    RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
                    accDistance = 0;
                }
            }
            catch (Exception e)
            {
                Log.Error("raw input toggle", e);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HOTKEY_ID_OVERLAY) ToggleOverlay();
                else if (id == HOTKEY_ID_CYCLE) CycleProfile();
                else if (id == HOTKEY_ID_MOVABLE) ToggleMovable();
                else if (id >= 9011 && id <= 9015) SwitchProfileByIndex(id - 9011);
            }
            else if (m.Msg == WM_INPUT && rawInputActive)
            {
                uint size = (uint)Marshal.SizeOf(typeof(RAWINPUT));
                uint got = GetRawInputData(m.LParam, RID_INPUT, out var ri, ref size, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));
                if (got != uint.MaxValue && ri.Header.Type == RIM_TYPEMOUSE && (ri.Mouse.Flags & MOUSE_MOVE_ABSOLUTE) == 0 && Listens(ri.Header.Device))
                {
                    double x = ri.Mouse.LastX, y = ri.Mouse.LastY;
                    if (x != 0 || y != 0) accDistance += Math.Sqrt(x * x + y * y);
                }
            }
            else if (m.Msg == WM_INPUT_DEVICE_CHANGE && webReady)
            {
                liveDirty = true;
                Post(new JObject { ["event"] = "devices", ["devices"] = engine.Devices() });
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// Whether a mouse should move the live marker. A mouse the driver leaves alone, or one that is on a
        /// different profile than the one being plotted, would put the marker in the wrong place.
        /// (DPI normalisation needs no handling here: with it, measured output speed still maps back through
        /// the same curve, and the result is already in the curve's own speed unit.)
        /// </summary>
        bool Listens(IntPtr device)
        {
            if (liveDirty)
            {
                liveDirty = false;
                try
                {
                    var cfg = engine.Active;
                    var names = new HashSet<string>(cfg.profiles.Select(p => p.name));
                    string first = cfg.profiles[0].name;
                    string plotted = (engine.DriverTwin() ?? cfg.profiles[0]).name;
                    var map = new Dictionary<IntPtr, bool>();
                    foreach (var dev in MultiHandleDevice.GetList())
                    {
                        var own = cfg.devices.Find(d => d.id == dev.id);
                        bool disabled = own != null ? own.config.disable : cfg.defaultDeviceConfig.disable;
                        string prof = own == null || string.IsNullOrEmpty(own.profile) || !names.Contains(own.profile) ? first : own.profile;
                        foreach (var h in dev.handles) map[h] = !disabled && prof == plotted;
                    }
                    liveHandles = map;
                }
                catch (Exception e)
                {
                    Log.Error("live devices", e);
                }
            }
            // Unknown handles (injected input, devices that appeared a moment ago) are counted rather than dropped.
            return !liveHandles.TryGetValue(device, out bool on) || on;
        }

        /// <summary>Turns the distance moved since the last tick into a speed and sends it to the plot.</summary>
        void FlushInput()
        {
            if (!rawInputActive || WindowState == FormWindowState.Minimized)
            {
                accDistance = 0;
                return;
            }
            double ms = inputClock.Elapsed.TotalMilliseconds;
            if (ms < 10) return;
            inputClock.Restart();
            double dist = accDistance;
            accDistance = 0;

            lastOut = dist / Math.Min(ms, 100);
            lastIn = lastOut > 0 ? engine.InputFromOutput(lastOut) : 0;
            if (lastIn > 0) engine.Histogram.Record(lastIn, engine.MaxSpeed);
            long now = Environment.TickCount;
            if (lastIn >= peakIn) { peakIn = lastIn; peakAt = now; }
            else if (now - peakAt > 3000) peakIn = lastIn;

            if (lastOut <= 0 && lastSentZero) return;
            lastSentZero = lastOut <= 0;
            if (webReady && WindowState != FormWindowState.Minimized && rawInputActive)
                Post(new JObject { ["event"] = "input", ["in"] = Math.Round(lastIn, 3), ["out"] = Math.Round(lastOut, 3) });
        }
    }
}
