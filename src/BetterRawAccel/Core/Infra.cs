using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace BetterRawAccel.Core
{
    public static class Paths
    {
        public static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterRawAccel");

        /// <summary>Driver settings. Kept next to the exe so the bundled writer.exe can apply them at logon.</summary>
        public static string SettingsFile => Path.Combine(AppDir, "settings.json");
        public static string AppSettingsFile => Path.Combine(DataDir, "app.json");
        public static string LogFile => Path.Combine(DataDir, "error.log");
        public static string UiDir => Path.Combine(AppDir, "ui");
        public static string WebViewDir => Path.Combine(DataDir, "WebView2");
        public static string UpdateDir => Path.Combine(DataDir, "updates");
        public static string ExePath => Assembly.GetExecutingAssembly().Location;
    }

    public static class AppInfo
    {
        public const string Name = "Better Raw Accel";
        public const string Repo = "jeelnadaa/better-rawaccel";
        public const string UpstreamRepo = "RawAccelOfficial/rawaccel";

        public static Version Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        /// <summary>Local builds are 0.0.0 and never update themselves.</summary>
        public static bool IsDevBuild => Version.Major == 0 && Version.Minor == 0 && Version.Build == 0;
    }

    public static class Log
    {
        static readonly object Gate = new object();
        const long MaxBytes = 512 * 1024;

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Paths.DataDir);
                    var file = Paths.LogFile;
                    if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                    {
                        var old = file + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(file, old);
                    }
                    File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{AppInfo.Version} {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Logging must never take the app down.
            }
        }

        public static void Error(string where, Exception e) => Write($"{where}: {e}");
    }

    public static class AtomicFile
    {
        /// <summary>Write to a temp file in the same folder, then swap, so a crash never leaves half a file.</summary>
        public static void WriteAllText(string path, string text)
        {
            var dir = Path.GetDirectoryName(path);
            Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }

    public sealed class AppSettings
    {
        public string Theme = "paper";
        public string Font = "satoshi";
        public bool AutoUpdate = true;
        public bool ApplyOnStart = false;
        public bool McpEnabled = true;
        public int McpPort = 47700;
        /// <summary>Only sets the plot's x range (DPI x 0.05 counts/ms), like the original GUI's DPI box.</summary>
        public int PlotDpi = 1600;
        public int PlotPollRate = 1000;
        public bool HelpSeen = false;
        /// <summary>sens, vel or gain.</summary>
        public string PlotView = "sens";
        public int[] Window;          // x, y, width, height
        public bool Maximized = false;
        public bool OverlayEnabled = true;
        public string OverlayPosition = "top-right";
        public bool OverlayAutoFade = false;
        public string OverlayHotkey = "Ctrl+Alt+O";
        public bool OverlayMovable = false;
        public int OverlayCustomX = -1;
        public int OverlayCustomY = -1;

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(Paths.AppSettingsFile))
                {
                    var s = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(Paths.AppSettingsFile));
                    if (s != null) return s.Sanitized();
                }
            }
            catch (Exception e)
            {
                Log.Error("app settings load", e);
            }
            return new AppSettings();
        }

        public AppSettings Sanitized()
        {
            if (McpPort < 1024 || McpPort > 65535) McpPort = 47700;
            if (PlotDpi < 100 || PlotDpi > 64000) PlotDpi = 1600;
            if (PlotPollRate < 125 || PlotPollRate > 8000) PlotPollRate = 1000;
            if (string.IsNullOrWhiteSpace(Theme)) Theme = "paper";
            if (string.IsNullOrWhiteSpace(Font)) Font = "satoshi";
            if (PlotView != "vel" && PlotView != "gain") PlotView = "sens";
            if (OverlayPosition != "top-left" && OverlayPosition != "bottom-right" && OverlayPosition != "top-center" && OverlayPosition != "custom")
                OverlayPosition = "top-right";
            if (string.IsNullOrWhiteSpace(OverlayHotkey)) OverlayHotkey = "Ctrl+Alt+O";
            return this;
        }

        public void Save()
        {
            try
            {
                AtomicFile.WriteAllText(Paths.AppSettingsFile, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch (Exception e)
            {
                Log.Error("app settings save", e);
            }
        }
    }
}
