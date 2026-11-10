using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using BetterRawAccel.Core;

namespace BetterRawAccel
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            using (var mutex = new Mutex(true, "BetterRawAccel.SingleInstance", out bool first))
            {
                if (!first)
                {
                    MessageBox.Show("Better Raw Accel is already running.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Numbers in settings always use a dot, whatever the Windows region is.
                CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
                CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Crash(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var app = AppSettings.Load();
                var engine = new Engine(app);
                try
                {
                    engine.Init();
                }
                catch (Exception e)
                {
                    Log.Error("start", e);
                    DriverProblem(e);
                    return;
                }

                Application.Run(new MainForm(app, engine));
                GC.KeepAlive(mutex);
            }
        }

        static void Crash(Exception e)
        {
            Log.Write("unhandled: " + (e?.ToString() ?? "unknown error"));
            MessageBox.Show(
                (e?.Message ?? "Unknown error") + "\n\nDetails were added to:\n" + Paths.LogFile,
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        static void DriverProblem(Exception e)
        {
            var installer = Path.Combine(Paths.AppDir, "installer.exe");
            bool notInstalled = e.Message.IndexOf("not installed", StringComparison.OrdinalIgnoreCase) >= 0;

            if (notInstalled && File.Exists(installer))
            {
                var answer = MessageBox.Show(
                    "The Raw Accel driver is not installed.\n\n" +
                    "Install it now? Windows will ask for administrator permission, and the driver starts working after a restart.",
                    AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer == DialogResult.Yes)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true, WorkingDirectory = Paths.AppDir });
                    }
                    catch (Exception ex)
                    {
                        Log.Error("installer", ex);
                        MessageBox.Show("The installer could not be started:\n" + ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                return;
            }

            MessageBox.Show(
                "Raw Accel could not be reached:\n\n" + e.Message.Trim() +
                "\n\nIf you just installed or updated the driver, restart Windows and try again.",
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
