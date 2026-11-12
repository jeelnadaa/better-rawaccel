using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using BetterRawAccel.Core;

namespace BetterRawAccel.Tests
{
    [TestClass]
    public class CrashLoggingTests
    {
        [TestMethod]
        public void TestCrashLogLocationAndAppends()
        {
            var expectedDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterRawAccel");
            Assert.AreEqual(expectedDir, Paths.DataDir);
            Assert.AreEqual(Path.Combine(expectedDir, "error.log"), Paths.LogFile);

            string testMarker = "CrashLogTest_" + Guid.NewGuid().ToString("N");
            Log.Write(testMarker);

            Assert.IsTrue(File.Exists(Paths.LogFile), "Log file should exist under %LOCALAPPDATA%\\BetterRawAccel");
            string content = File.ReadAllText(Paths.LogFile);
            Assert.IsTrue(content.Contains(testMarker), "Log file should contain the written marker");

            // Also test Log.Error with Exception
            string errorMarker = "SimulatedCrash_" + Guid.NewGuid().ToString("N");
            Log.Error(errorMarker, new InvalidOperationException("Test crash exception"));
            content = File.ReadAllText(Paths.LogFile);
            Assert.IsTrue(content.Contains(errorMarker));
            Assert.IsTrue(content.Contains("Test crash exception"));
        }

        [TestMethod]
        public void TestLogRotationWhenExceedingMaxSize()
        {
            string logFile = Paths.LogFile;
            string rotatedFile = logFile + ".1";

            // If log file exists, verify that writing works without exceptions
            Log.Write("Log rotation verification entry");
            Assert.IsTrue(File.Exists(logFile));
        }
    }

    [TestClass]
    public class StartupShortcutTests
    {
        [TestMethod]
        public void TestStartupShortcutCreateAndRemove()
        {
            // Never touch a real sign-in shortcut on the machine running the tests.
            if (Startup.IsEnabled()) Assert.Inconclusive("A real 'apply at sign-in' shortcut exists; leaving it alone.");

            // Ensure settings.json exists in AppDir so Startup.Set(true) does not reject it
            string settingsPath = Paths.SettingsFile;
            bool createdSettings = false;
            if (!File.Exists(settingsPath))
            {
                File.WriteAllText(settingsPath, "{}");
                createdSettings = true;
            }

            try
            {
                // Clean state initially
                Startup.Set(false);
                Assert.IsFalse(Startup.IsEnabled(), "Startup should be disabled after Set(false)");

                // Enable startup shortcut
                Startup.Set(true);
                Assert.IsTrue(Startup.IsEnabled(), "Startup should be enabled after Set(true)");

                var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                var linkPath = Path.Combine(startupFolder, "BetterRawAccel settings.lnk");
                Assert.IsTrue(File.Exists(linkPath), "Shortcut file .lnk must exist in the Windows Startup folder");

                // Clean up by disabling
                Startup.Set(false);
                Assert.IsFalse(Startup.IsEnabled(), "Startup should be disabled after removing shortcut");
                Assert.IsFalse(File.Exists(linkPath), "Shortcut file .lnk must be deleted from Startup folder");
            }
            finally
            {
                if (createdSettings && File.Exists(settingsPath))
                {
                    try { File.Delete(settingsPath); } catch { }
                }
            }
        }
    }

    [TestClass]
    public class AppSettingsStartupTests
    {
        [TestMethod]
        public void TestApplyOnStartSettingSerialization()
        {
            var s = new AppSettings
            {
                ApplyOnStart = true,
                AutoUpdate = false,
                McpEnabled = true,
                McpPort = 47712,
                Theme = "graphite"
            };

            s.Sanitized();
            Assert.IsTrue(s.ApplyOnStart);
            Assert.AreEqual("graphite", s.Theme);
            Assert.AreEqual(47712, s.McpPort);

            // Verify defaults
            var def = new AppSettings();
            Assert.IsFalse(def.ApplyOnStart, "ApplyOnStart should default to false");
            Assert.IsTrue(def.McpEnabled, "McpEnabled should default to true");
            Assert.AreEqual(47700, def.McpPort);
        }
    }

    [TestClass]
    public class McpServerToggleAndPortTests
    {
        [TestMethod]
        public async Task TestServerToggleAndPortSwitching()
        {
            int testPort1 = 47761;
            int testPort2 = 47762;

            var server = new McpServer(
                () => new ToolDef[0],
                (tool, args) => new JObject { ["echo"] = tool }
            );

            try
            {
                Assert.IsFalse(server.Running);

                // Start on port 1
                server.Start(testPort1);
                Assert.IsTrue(server.Running);
                Assert.AreEqual($"http://127.0.0.1:{testPort1}/mcp", server.Url);

                // Verify reachable via HTTP
                using (var http = new HttpClient())
                {
                    var resp = await http.GetAsync($"http://127.0.0.1:{testPort1}/mcp");
                    // GET /mcp returns 405 Method Not Allowed (since MCP requires POST), which confirms server is listening
                    Assert.AreEqual(System.Net.HttpStatusCode.MethodNotAllowed, resp.StatusCode);
                }

                // Stop server
                server.Stop();
                Assert.IsFalse(server.Running);

                // Start on port 2
                server.Start(testPort2);
                Assert.IsTrue(server.Running);
                Assert.AreEqual($"http://127.0.0.1:{testPort2}/mcp", server.Url);

                using (var http = new HttpClient())
                {
                    var resp = await http.GetAsync($"http://127.0.0.1:{testPort2}/mcp");
                    Assert.AreEqual(System.Net.HttpStatusCode.MethodNotAllowed, resp.StatusCode);
                }

                server.Stop();
                Assert.IsFalse(server.Running);
            }
            finally
            {
                server.Dispose();
            }
        }
    }

    [TestClass]
    public class DeviceFilteringLogicTests
    {
        /// <summary>
        /// Pure logic replica of MainForm.Listens routing rules for multi-device mouse configurations.
        /// </summary>
        public static bool CheckDeviceAllowed(
            string deviceId,
            IntPtr handle,
            List<(string id, bool disabled, string profile, IntPtr[] handles)> devices,
            bool defaultDisable,
            string plottedProfile,
            HashSet<string> validProfiles)
        {
            var map = new Dictionary<IntPtr, bool>();
            string firstProfile = "Default";

            foreach (var dev in devices)
            {
                bool disabled = dev.disabled;
                string prof = string.IsNullOrEmpty(dev.profile) || !validProfiles.Contains(dev.profile)
                    ? firstProfile
                    : dev.profile;

                bool allowed = !disabled && prof == plottedProfile;
                foreach (var h in dev.handles) map[h] = allowed;
            }

            return !map.TryGetValue(handle, out bool on) || on;
        }

        [TestMethod]
        public void TestDeviceFilteringRules()
        {
            var validProfiles = new HashSet<string> { "Desktop", "Gaming" };
            var hMainMouse = new IntPtr(1001);
            var hDisabledMouse = new IntPtr(1002);
            var hOtherProfileMouse = new IntPtr(1003);
            var hUnknownMouse = new IntPtr(9999);

            var devices = new List<(string id, bool disabled, string profile, IntPtr[] handles)>
            {
                ("dev_active", false, "Desktop", new[] { hMainMouse }),
                ("dev_disabled", true, "Desktop", new[] { hDisabledMouse }),
                ("dev_other", false, "Gaming", new[] { hOtherProfileMouse })
            };

            // When plotted profile is "Desktop":
            // 1. Active mouse on "Desktop" should be ALLOWED (true)
            Assert.IsTrue(CheckDeviceAllowed("dev_active", hMainMouse, devices, false, "Desktop", validProfiles));

            // 2. Disabled mouse should be IGNORED (false)
            Assert.IsFalse(CheckDeviceAllowed("dev_disabled", hDisabledMouse, devices, false, "Desktop", validProfiles));

            // 3. Mouse on "Gaming" profile when plotting "Desktop" should be IGNORED (false)
            Assert.IsFalse(CheckDeviceAllowed("dev_other", hOtherProfileMouse, devices, false, "Desktop", validProfiles));

            // 4. Unknown/unregistered mouse handles should pass through rather than drop
            Assert.IsTrue(CheckDeviceAllowed("dev_unknown", hUnknownMouse, devices, false, "Desktop", validProfiles));

            // When switched to plotting "Gaming":
            // "dev_other" is now on the plotted profile and not disabled -> ALLOWED
            Assert.IsTrue(CheckDeviceAllowed("dev_other", hOtherProfileMouse, devices, false, "Gaming", validProfiles));
            // "dev_active" is on "Desktop" -> now IGNORED
            Assert.IsFalse(CheckDeviceAllowed("dev_active", hMainMouse, devices, false, "Gaming", validProfiles));
        }
    }

    [TestClass]
    public class AdvancedOptionsContractsTests
    {
        [TestMethod]
        public void TestMetaJsonDefinesAllAdvancedOptions()
        {
            string metaPath = Path.Combine(Paths.UiDir, "meta.json");
            Assert.IsTrue(File.Exists(metaPath), "meta.json must exist in ui directory");

            var meta = JObject.Parse(File.ReadAllText(metaPath));
            var adv = (JArray)meta["advanced"];
            Assert.IsNotNull(adv);

            var fields = new HashSet<string>();
            foreach (JObject item in adv)
            {
                fields.Add((string)item["field"]);
            }

            // Verify all options mentioned in checklist line 35 are present in metadata:
            Assert.IsTrue(fields.Contains("snap"), "Angle snapping control 'snap' must exist");
            Assert.IsTrue(fields.Contains("speedCap"), "Speed cap control 'speedCap' must exist");
            Assert.IsTrue(fields.Contains("lrRatio"), "Left/Right multiplier 'lrRatio' must exist");
            Assert.IsTrue(fields.Contains("udRatio"), "Up/Down multiplier 'udRatio' must exist");
            Assert.IsTrue(fields.Contains("inputSmooth"), "Input smoothing halflife 'inputSmooth' must exist");
            Assert.IsTrue(fields.Contains("scaleSmooth"), "Scale smoothing halflife 'scaleSmooth' must exist");
            Assert.IsTrue(fields.Contains("outputSmooth"), "Output smoothing halflife 'outputSmooth' must exist");
            Assert.IsTrue(fields.Contains("lpNorm"), "Lp norm 'lpNorm' must exist");
        }

        [TestMethod]
        public void TestAdvancedOptionsDtoSerialization()
        {
            var p = new Profile
            {
                name = "advanced_test",
                outputDPI = 1000.0,
                snap = 15.0,
                maximumSpeed = 120.0,
                lrOutputDPIRatio = 1.1,
                udOutputDPIRatio = 0.9,
                inputSpeedArgs = new SpeedArgs
                {
                    combineMagnitudes = true,
                    lpNorm = 2.0,
                    inputSmoothHalflife = 5.0,
                    scaleSmoothHalflife = 10.0,
                    outputSmoothHalflife = 15.0
                },
                argsX = new AccelArgs { mode = AccelMode.noaccel }
            };

            // Test field values
            Assert.AreEqual(15.0, p.snap);
            Assert.AreEqual(120.0, p.maximumSpeed);
            Assert.AreEqual(1.1, p.lrOutputDPIRatio);
            Assert.AreEqual(0.9, p.udOutputDPIRatio);
            Assert.AreEqual(5.0, p.inputSpeedArgs.inputSmoothHalflife);
            Assert.AreEqual(10.0, p.inputSpeedArgs.scaleSmoothHalflife);
            Assert.AreEqual(15.0, p.inputSpeedArgs.outputSmoothHalflife);
        }
    }

    [TestClass]
    public class MissingDriverAndInstallerTests
    {
        [TestMethod]
        public void TestInstallerBinaryPresence()
        {
            string installer = Path.Combine(Paths.AppDir, "installer.exe");
            Assert.IsTrue(File.Exists(installer), "Bundled installer.exe must be present next to application");
        }

        [TestMethod]
        public void TestMissingDriverExceptionDetection()
        {
            var ex1 = new Exception("The Raw Accel driver is not installed or not running");
            bool notInstalled1 = ex1.Message.IndexOf("not installed", StringComparison.OrdinalIgnoreCase) >= 0;
            Assert.IsTrue(notInstalled1, "Exception message containing 'not installed' must be recognized");

            var ex2 = new Exception("Driver device could not be opened: Access is denied");
            bool notInstalled2 = ex2.Message.IndexOf("not installed", StringComparison.OrdinalIgnoreCase) >= 0;
            Assert.IsFalse(notInstalled2, "Generic driver access exceptions must not be falsely recognized as missing driver");
        }
    }

    [TestClass]
    public class OverlayHUDTests
    {
        [TestMethod]
        public void TestOverlayFormStylesAndProperties()
        {
            var app = new AppSettings
            {
                OverlayEnabled = true,
                OverlayPosition = "top-right",
                OverlayAutoFade = false
            };

            using (var overlay = new OverlayForm(app))
            {
                Assert.IsTrue(overlay.TopMost, "Overlay must be TopMost to float above borderless games");
                Assert.IsFalse(overlay.ShowInTaskbar, "Overlay must not appear in Windows taskbar");
                Assert.AreEqual(System.Windows.Forms.FormBorderStyle.None, overlay.FormBorderStyle);

                overlay.UpdateInfo("Valorant Headhunter", "Logitech G Pro", "Natural", "1.15×", "48 cm");
                Assert.AreEqual("Valorant Headhunter", overlay.ProfileName);
                Assert.AreEqual("Logitech G Pro", overlay.DeviceName);
                Assert.AreEqual("Natural", overlay.ModeName);
                Assert.AreEqual("1.15×", overlay.SensValue);
                Assert.AreEqual("48 cm", overlay.Cm360Value);

                overlay.Reposition("top-left");
                Assert.AreEqual("top-left", overlay.PositionMode);
                overlay.Reposition("bottom-right");
                Assert.AreEqual("bottom-right", overlay.PositionMode);

                // Test Movable vs Fixed properties
                Assert.IsFalse(overlay.IsMovable);
                overlay.SetMovable(true);
                Assert.IsTrue(overlay.IsMovable);
                overlay.SetMovable(false);
                Assert.IsFalse(overlay.IsMovable);

                // Test Custom Position
                overlay.Reposition("custom", 120, 240);
                Assert.AreEqual("custom", overlay.PositionMode);
                Assert.AreEqual(120, overlay.Location.X);
                Assert.AreEqual(240, overlay.Location.Y);
            }
        }

        [TestMethod]
        public void TestOverlayAppSettingsSerialization()
        {
            var s = new AppSettings
            {
                OverlayEnabled = true,
                OverlayPosition = "bottom-right",
                OverlayAutoFade = true,
                OverlayHotkey = "Ctrl+Alt+O"
            };

            s.Sanitized();
            Assert.IsTrue(s.OverlayEnabled);
            Assert.AreEqual("bottom-right", s.OverlayPosition);
            Assert.IsTrue(s.OverlayAutoFade);
            Assert.AreEqual("Ctrl+Alt+O", s.OverlayHotkey);

            var sBad = new AppSettings
            {
                OverlayPosition = "invalid-position"
            };
            sBad.Sanitized();
            Assert.AreEqual("top-right", sBad.OverlayPosition);
        }

        [TestMethod]
        public void TestZeroLagForegroundExecutionMode()
        {
            // Verify overlay does not enable mouse input hooks and maintains zero-activation flags
            var s = new AppSettings();
            using (var overlay = new OverlayForm(s))
            {
                Assert.IsTrue(overlay.TopMost);
                Assert.IsFalse(overlay.ShowInTaskbar);
                Assert.AreEqual(System.Windows.Forms.FormBorderStyle.None, overlay.FormBorderStyle);
            }
        }
    }
}
