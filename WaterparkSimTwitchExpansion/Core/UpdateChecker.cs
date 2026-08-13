using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using Newtonsoft.Json;

namespace WaterparkSimTwitchExpansion.Core
{
    /// <summary>
    /// Checks GitHub Releases for a newer build of this mod and, on request, downloads and stages
    /// it for install. All network/disk work runs on a ThreadPool thread (same convention as
    /// OverlayServer/TwitchFollowerProvider) and reports back through MainThreadDispatcher - never
    /// touch UnityEngine from any of this class's own methods except via that hop.
    ///
    /// The running plugin DLL is loaded and locked by the game process for as long as it's open,
    /// so an update can't be applied in place while playing. Instead BeginInstall() downloads the
    /// release zip, extracts it to a staging folder under BepInEx's own cache path, and writes +
    /// launches a small detached batch script that waits for THIS game process to exit, then
    /// copies the staged files over the live install (mirroring the manual "extract into the game
    /// folder" step from SETUP.md) and deletes itself. The user just needs to close and reopen the
    /// game normally - no separate installer program to run by hand.
    /// </summary>
    public sealed class UpdateChecker
    {
        private const string RepoOwner = "musicman0917";
        private const string RepoName = "WaterparkSimTwitchExpansion";
        private const string ReleasesApiUrl = "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";

        public enum Status { NotChecked, Checking, UpToDate, UpdateAvailable, CheckFailed }
        public enum Install { Idle, Downloading, Staged, Failed }

        private readonly ManualLogSource _log;
        private readonly MainThreadDispatcher _dispatcher;
        private readonly OnScreenNotifier _notifier;
        private readonly Version _currentVersion;
        private readonly HttpClient _http;

        private string _downloadUrl;
        private volatile bool _installStarted;

        public Status CheckStatus { get; private set; } = Status.NotChecked;
        public Install InstallStatus { get; private set; } = Install.Idle;
        public string LatestVersionText { get; private set; }
        public string ReleaseUrl { get; private set; }
        public string InstallError { get; private set; }
        public bool CanInstall => _downloadUrl != null;

        public UpdateChecker(ManualLogSource log, MainThreadDispatcher dispatcher, OnScreenNotifier notifier, string currentVersionText)
        {
            _log = log;
            _dispatcher = dispatcher;
            _notifier = notifier;
            _currentVersion = ParseVersion(currentVersionText);

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            // GitHub's API rejects requests with no User-Agent header.
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("WaterparkSimTwitchExpansion-UpdateChecker");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        /// <summary>Fire-and-forget - kicks off the GitHub check on a background thread. Safe to
        /// call from Plugin.Load() (Unity's main thread).</summary>
        public void CheckForUpdateAsync()
        {
            if (CheckStatus == Status.Checking)
            {
                return;
            }

            CheckStatus = Status.Checking;
            ThreadPool.QueueUserWorkItem(_ => RunCheck());
        }

        private void RunCheck()
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
                using var response = _http.Send(request);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // No releases published yet - not an error, just nothing to compare against.
                    _dispatcher.Enqueue(() => CheckStatus = Status.UpToDate);
                    return;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _dispatcher.Enqueue(() =>
                    {
                        CheckStatus = Status.CheckFailed;
                        _log.LogWarning($"UpdateChecker: GitHub release check failed with HTTP {(int)response.StatusCode}.");
                    });
                    return;
                }

                using var stream = response.Content.ReadAsStream();
                using var reader = new StreamReader(stream);
                var release = JsonConvert.DeserializeObject<GitHubRelease>(reader.ReadToEnd());

                var latest = ParseVersion(release?.tag_name);
                string zipUrl = null;
                if (release?.assets != null)
                {
                    foreach (var asset in release.assets)
                    {
                        if (asset?.name != null && asset.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            zipUrl = asset.browser_download_url;
                            break;
                        }
                    }
                }

                _dispatcher.Enqueue(() =>
                {
                    LatestVersionText = release?.tag_name;
                    ReleaseUrl = release?.html_url;
                    _downloadUrl = zipUrl;

                    if (latest != null && _currentVersion != null && latest > _currentVersion)
                    {
                        CheckStatus = Status.UpdateAvailable;
                        _log.LogInfo($"UpdateChecker: a newer version is available ({release.tag_name}, currently running v{_currentVersion}) - {release.html_url}");
                        _notifier?.Show(zipUrl != null
                            ? $"Update available: {release.tag_name} - open F9 settings to install"
                            : $"Update available: {release.tag_name} - see {release.html_url}");
                    }
                    else
                    {
                        CheckStatus = Status.UpToDate;
                    }
                });
            }
            catch (Exception e)
            {
                _dispatcher.Enqueue(() =>
                {
                    CheckStatus = Status.CheckFailed;
                    _log.LogWarning($"UpdateChecker: update check failed - {e.Message}");
                });
            }
        }

        /// <summary>Downloads and stages the update in the background, then launches a detached
        /// helper script that finishes the install after the game process exits. Safe to call
        /// repeatedly (a second call while one is already in flight is a no-op).</summary>
        public void BeginInstall()
        {
            if (_installStarted || _downloadUrl == null)
            {
                return;
            }

            _installStarted = true;
            InstallStatus = Install.Downloading;
            var zipUrl = _downloadUrl;
            var versionLabel = LatestVersionText;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    StageUpdate(zipUrl);
                    _dispatcher.Enqueue(() =>
                    {
                        InstallStatus = Install.Staged;
                        _log.LogInfo($"UpdateChecker: {versionLabel} downloaded and staged - close the game normally to finish installing it.");
                        _notifier?.Show($"{versionLabel} staged - close the game to finish installing");
                    });
                }
                catch (Exception e)
                {
                    _dispatcher.Enqueue(() =>
                    {
                        InstallStatus = Install.Failed;
                        InstallError = e.Message;
                        _installStarted = false;
                        _log.LogError($"UpdateChecker: failed to stage update - {e}");
                    });
                }
            });
        }

        private void StageUpdate(string zipUrl)
        {
            var workDir = Path.Combine(Paths.CachePath, "WaterparkSimTwitchExpansion_update");
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }
            Directory.CreateDirectory(workDir);

            var zipPath = Path.Combine(workDir, "update.zip");
            using (var request = new HttpRequestMessage(HttpMethod.Get, zipUrl))
            using (var response = _http.Send(request))
            {
                response.EnsureSuccessStatusCode();
                using var sourceStream = response.Content.ReadAsStream();
                using var fileStream = File.Create(zipPath);
                sourceStream.CopyTo(fileStream);
            }

            var stagedDir = Path.Combine(workDir, "staged");
            Directory.CreateDirectory(stagedDir);
            ZipFile.ExtractToDirectory(zipPath, stagedDir, overwriteFiles: true);

            var gameRoot = Paths.GameRootPath;
            var pid = Process.GetCurrentProcess().Id;
            var scriptPath = Path.Combine(workDir, "apply_update.bat");
            File.WriteAllText(scriptPath, BuildApplyScript(pid, stagedDir, gameRoot, zipPath));

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(psi);
        }

        // Waits for this game process to exit (checked once a second, capped at ~12 hours so an
        // orphaned script doesn't run forever), copies the staged files over the live install the
        // same way the manual "extract into the game folder" step does, then removes the staging
        // folder and itself. `del "%~f0"` on the script's own path works even while it's the file
        // cmd.exe is currently executing - Windows allows deleting a running batch file, it just
        // can't be rewritten until the interpreter releases its handle on exit.
        private static string BuildApplyScript(int pid, string stagedDir, string gameRoot, string zipPath)
        {
            return
                "@echo off\r\n" +
                "setlocal\r\n" +
                $"set COUNT=0\r\n" +
                ":waitloop\r\n" +
                $"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL\r\n" +
                "if %ERRORLEVEL%==0 (\r\n" +
                "    set /a COUNT+=1\r\n" +
                "    if %COUNT% GEQ 43200 goto :eof\r\n" +
                "    timeout /t 1 /nobreak >NUL\r\n" +
                "    goto waitloop\r\n" +
                ")\r\n" +
                $"xcopy \"{stagedDir}\\*\" \"{gameRoot}\\\" /E /I /Y /Q\r\n" +
                $"rmdir /S /Q \"{stagedDir}\"\r\n" +
                $"del \"{zipPath}\"\r\n" +
                "del \"%~f0\"\r\n";
        }

        private static Version ParseVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var trimmed = text.Trim();
            if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(1);
            }

            return Version.TryParse(trimmed, out var version) ? version : null;
        }

        private sealed class GitHubRelease
        {
            public string tag_name { get; set; }
            public string html_url { get; set; }
            public List<GitHubReleaseAsset> assets { get; set; }
        }

        private sealed class GitHubReleaseAsset
        {
            public string name { get; set; }
            public string browser_download_url { get; set; }
        }
    }
}
