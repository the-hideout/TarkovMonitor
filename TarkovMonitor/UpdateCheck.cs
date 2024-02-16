
using Refit;

namespace TarkovMonitor
{
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
            try
            {
                var release = await api.GetLatestRelease();
                Version remoteVersion = new Version(release.tag_name);
                Version localVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? throw new Exception("Could not retrieve version from assembly");
                //System.Diagnostics.Debug.WriteLine(localVersion.ToString());

                if (localVersion.CompareTo(remoteVersion) == -1)
                {
                    NewVersion?.Invoke(null, new() { Version = remoteVersion, Uri = new(release.html_url) });
                }
            }
            catch (ApiException ex)
            {
                Error?.Invoke(null, new(new Exception($"Invalid GitHub API response code: {ex.Message}"), "checking for new version"));
            }
            catch (Exception ex)
            {
                Error?.Invoke(null, new(new Exception($"GitHub API error: {ex.Message}"), "checking for new version"));
            }
        }

        public class ReleaseData
        {
            public string tag_name { get; set; }
            public string html_url { get; set; }
        }
    }

    public class NewVersionEventArgs : EventArgs
    {
        public Version Version { get; set; }
        public Uri Uri { get; set; }
    }
}

// to release a new version:
// Checkout main/master (assuming everything is merged already)
// tag the current commit (eg. git tag 1.0.1.2)
// push the tag to GitHub (git push origin 1.0.1.2)
