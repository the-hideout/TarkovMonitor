using Refit;

namespace TarkovMonitor
{
    public enum UpdateCheckResult
    {
        UpToDate,
        UpdateAvailable,
        Failed,
    }

    internal class UpdateCheck
    {
        internal interface IGitHubAPI
        {
            [Get("/releases/latest")]
            [Headers("user-agent: tarkov-monitor")]
            Task<ReleaseData> GetLatestRelease();
        }

        private static readonly string repo = "the-hideout/TarkovMonitor";
        private static readonly System.Timers.Timer updateCheckTimer;

        private static readonly IGitHubAPI api = RestService.For<IGitHubAPI>($"https://api.github.com/repos/{repo}");

        public static event EventHandler<NewVersionEventArgs>? NewVersion;
        public static event EventHandler<ExceptionEventArgs>? Error;

        static UpdateCheck()
        {
            updateCheckTimer = new(TimeSpan.FromDays(1).TotalMilliseconds)
            {
                AutoReset = true,
                Enabled = true
            };
            updateCheckTimer.Elapsed += UpdateCheckTimer_Elapsed;
        }

        private static void UpdateCheckTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            CheckForNewVersion();
        }

        public static async void CheckForNewVersion()
        {
            await CheckForNewVersionAsync();
        }

        /// <summary>
        /// Awaitable form of the check, so a manual "check for updates" action
        /// can tell the user that the installed version is already current.
        /// Errors are still reported through <see cref="Error"/>.
        /// </summary>
        public static async Task<UpdateCheckResult> CheckForNewVersionAsync()
        {
            try
            {
                var release = await api.GetLatestRelease();
                if (!Version.TryParse(release.tag_name, out var remoteVersion))
                {
                    throw new Exception($"Could not read a version number from the release tag \"{release.tag_name}\".");
                }
                Version localVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? throw new Exception("Could not retrieve version from assembly");
                //System.Diagnostics.Debug.WriteLine(localVersion.ToString());

                if (localVersion.CompareTo(remoteVersion) != -1)
                {
                    return UpdateCheckResult.UpToDate;
                }

                NewVersion?.Invoke(null, new()
                {
                    Version = remoteVersion,
                    Uri = new(release.html_url),
                    Asset = SelectArchiveAsset(release),
                });
                return UpdateCheckResult.UpdateAvailable;
            }
            catch (ApiException ex)
            {
                Error?.Invoke(null, new(new Exception($"Invalid GitHub API response code: {ex.StatusCode}.", ex), "checking for new version"));
                return UpdateCheckResult.Failed;
            }
            catch (Exception ex)
            {
                Error?.Invoke(null, new(new Exception("GitHub API error.", ex), "checking for new version"));
                return UpdateCheckResult.Failed;
            }
        }

        /// <summary>
        /// Finds the release archive the in-app updater can install. Releases
        /// built before the updater existed, or with an unexpected set of
        /// attachments, simply return null and fall back to the download page.
        /// </summary>
        private static ReleaseAsset? SelectArchiveAsset(ReleaseData release)
        {
            var archives = release.assets
                .Where(asset => !string.IsNullOrEmpty(asset.browser_download_url)
                    && asset.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .ToList();

            return archives.FirstOrDefault(asset => string.Equals(
                    asset.name,
                    "TarkovMonitor.zip",
                    StringComparison.OrdinalIgnoreCase))
                ?? archives.FirstOrDefault();
        }

        public class ReleaseData
        {
            public string tag_name { get; set; } = "";
            public string html_url { get; set; } = "";
            public List<ReleaseAsset> assets { get; set; } = new();
        }

        public class ReleaseAsset
        {
            public string name { get; set; } = "";
            public string browser_download_url { get; set; } = "";
            public long size { get; set; }
        }
    }

    public class NewVersionEventArgs : EventArgs
    {
        public Version Version { get; set; }
        public Uri Uri { get; set; }

        /// <summary>
        /// The archive the update can be installed from, or null when the
        /// release has none and the user has to download it themselves.
        /// </summary>
        internal UpdateCheck.ReleaseAsset? Asset { get; set; }
    }
}

// to release a new version:
// Checkout main/master (assuming everything is merged already)
// bump AssemblyVersion in TarkovMonitor.csproj to match the tag you are about to
// create; the in-app update check compares the release tag against that value
// tag the current commit (eg. git tag 1.0.1.2)
// push the tag to GitHub (git push origin 1.0.1.2)
