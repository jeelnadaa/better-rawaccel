using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BetterRawAccel.Core
{
    /// <summary>
    /// Checks GitHub releases, downloads the release zip, verifies it against the published SHA-256,
    /// and swaps the app files with a small script once this process has exited.
    /// </summary>
    public sealed class Updater
    {
        public const string AssetName = "BetterRawAccel-win-x64.zip";
        const string HashName = AssetName + ".sha256";

        static readonly HttpClient Http = MakeClient();
        string stagedDir;
        bool busy;

        /// <summary>idle | checking | current | available | downloading | ready | error</summary>
        public string State { get; private set; } = "idle";
        public string Latest { get; private set; }
        public string Error { get; private set; }
        public string Notes { get; private set; }
        public DateTime? CheckedAt { get; private set; }
        public event Action Changed;
        public event Action RestartRequested;

        static HttpClient MakeClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("BetterRawAccel-Updater");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        void Set(string state, string error = null)
        {
            State = state;
            Error = error;
            Changed?.Invoke();
        }

        public JObject Dto() => new JObject
        {
            ["state"] = State,
            ["current"] = AppInfo.Version.ToString(3),
            ["latest"] = Latest,
            ["error"] = Error,
            ["notes"] = Notes,
            ["checkedAt"] = CheckedAt?.ToString("HH:mm:ss"),
            ["devBuild"] = AppInfo.IsDevBuild,
        };

        static Version ParseTag(string tag)
        {
            var s = (tag ?? "").TrimStart('v', 'V');
            return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
        }

        /// <summary>Looks for a newer release. With download, also fetches and verifies it so it is ready to install.</summary>
        public async Task CheckAsync(bool download)
        {
            if (busy) return;
            if (State == "ready" && stagedDir != null && Directory.Exists(stagedDir)) return;
            busy = true;
            try
            {
                Set("checking");
                JObject rel;
                using (var resp = await Http.GetAsync($"https://api.github.com/repos/{AppInfo.Repo}/releases/latest"))
                {
                    CheckedAt = DateTime.Now;
                    if (resp.StatusCode == HttpStatusCode.NotFound) { Latest = null; Set("current"); return; }   // no releases published yet
                    if ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 429) throw new InvalidOperationException("GitHub is rate-limiting update checks; try again later");
                    resp.EnsureSuccessStatusCode();
                    rel = JObject.Parse(await resp.Content.ReadAsStringAsync());
                }
                var tag = (string)rel["tag_name"];
                var latest = ParseTag(tag) ?? throw new InvalidOperationException("The latest release has no usable version tag");
                Latest = latest.ToString(3);
                Notes = (string)rel["name"];

                if (latest <= AppInfo.Version) { Set("current"); return; }

                var assets = rel["assets"] as JArray ?? new JArray();
                var zip = assets.FirstOrDefault(x => (string)x["name"] == AssetName);
                var hash = assets.FirstOrDefault(x => (string)x["name"] == HashName);
                if (zip == null || hash == null) throw new InvalidOperationException($"Release {tag} has no {AssetName} with a .sha256 file");

                if (!download) { Set("available"); return; }
                if (AppInfo.IsDevBuild) throw new InvalidOperationException("This is a local development build, so it does not replace itself. Download the release from GitHub.");

                Set("downloading");
                var dir = Path.Combine(Paths.UpdateDir, Latest);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                var zipPath = Path.Combine(dir, AssetName);

                using (var resp = await Http.GetAsync((string)zip["browser_download_url"], HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    using (var fs = File.Create(zipPath)) await resp.Content.CopyToAsync(fs);
                }

                var expected = (await Http.GetStringAsync((string)hash["browser_download_url"])).Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
                string actual;
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(zipPath))
                    actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                if (expected.Length != 64 || actual != expected)
                {
                    Directory.Delete(dir, true);
                    throw new InvalidOperationException("The download did not match its published SHA-256, so it was discarded");
                }

                var stage = Path.Combine(dir, "stage");
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, stage));
                File.Delete(zipPath);
                if (!File.Exists(Path.Combine(stage, Path.GetFileName(Paths.ExePath))))
                {
                    Directory.Delete(dir, true);
                    throw new InvalidOperationException("The release zip does not contain the app");
                }
                stagedDir = stage;
                Set("ready");
            }
            catch (Exception e)
            {
                Log.Error("update", e);
                var msg = e is HttpRequestException || e is TaskCanceledException ? "Could not reach GitHub" : e.Message;
                Set("error", msg);
            }
            finally
            {
                busy = false;
            }
        }

        public bool Ready => State == "ready" && stagedDir != null && Directory.Exists(stagedDir);

        /// <summary>Ask the window to close; it calls LaunchSwap on the way out.</summary>
        public void ApplyAndRestartSoon() => RestartRequested?.Invoke();

        /// <summary>
        /// Starts a hidden script that waits for this process to exit, copies the new files over the app folder
        /// (keeping settings.json), optionally starts the new version, and removes the staging folder.
        /// </summary>
        public void LaunchSwap(bool restart)
        {
            if (!Ready) return;
            try
            {
                int pid = Process.GetCurrentProcess().Id;
                var script = Path.Combine(Paths.UpdateDir, "apply-update.cmd");
                var exe = Path.Combine(Paths.AppDir, Path.GetFileName(Paths.ExePath));
                var lines = new[]
                {
                    "@echo off",
                    ":wait",
                    $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul",
                    "if not errorlevel 1 (ping -n 2 127.0.0.1 >nul & goto wait)",
                    $"robocopy \"{stagedDir}\" \"{Paths.AppDir}\" /E /R:5 /W:1 /XF settings.json >nul",
                    restart ? $"start \"\" \"{exe}\"" : "rem no restart",
                    $"rmdir /s /q \"{Path.GetDirectoryName(stagedDir)}\"",
                    "del \"%~f0\"",
                };
                File.WriteAllLines(script, lines);
                Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Paths.UpdateDir,
                });
                Log.Write($"update to {Latest} staged; swapping after exit");
            }
            catch (Exception e)
            {
                Log.Error("update swap", e);
            }
        }
    }
}
