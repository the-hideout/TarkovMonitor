
using System.Text.Json.Nodes;

namespace TarkovMonitor
{
    internal class UpdateCheck
    {
        private static string repo = "the-hideout/TarkovMonitor";
        private static readonly HttpClient client = new();

        public static event EventHandler<NewVersionEventArgs> NewVersion;
        public static event EventHandler<UpdateCheckErrorEventArgs> Error;

        public static async void CheckForNewVersion()
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
                request.Headers.Add("user-agent", "tarkov-monitor");
                var response = await client.SendAsync(request);
                response.EnsureSuccessStatusCode();
                JsonNode latestRelease = JsonNode.Parse(await response.Content.ReadAsStringAsync());

                Version remoteVersion = new Version(latestRelease["tag_name"].ToString());
                Version localVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

                if (localVersion.CompareTo(remoteVersion) == -1)
                {
                    NewVersion?.Invoke(null, new() { Version = remoteVersion, Uri = new(latestRelease["html_url"].ToString()) });
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(null, new() { Exception = ex });
            }
        }
    }

    public class NewVersionEventArgs : EventArgs
    {
        public Version Version { get; set; }
        public Uri Uri { get; set; }
    }
    public class UpdateCheckErrorEventArgs : EventArgs 
    { 
        public Exception Exception { get; set;} 
    }
}
