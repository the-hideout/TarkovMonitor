using System.Text;
using System.Diagnostics;
using System.Text.Json;

namespace TarkovMonitor
{
    internal class TarkovTracker
    {
        static readonly string apiUrl = "https://tarkovtracker.io/api/v2";
        static string token = "";
        static readonly HttpClient client = new();

        public static event EventHandler<EventArgs> Initialized;

        public static async Task Init()
        {
            if (Properties.Settings.Default.tarkovTrackerToken.Length > 0)
            {
                token = Properties.Settings.Default.tarkovTrackerToken;
            }
            Initialized?.Invoke(typeof(TarkovTracker), new EventArgs());
        }

        private static HttpRequestMessage GetRequest(string path, string apiToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, apiUrl + path);
            request.Headers.Add("Authorization", "Bearer " + apiToken);
            return request;
        }

        private static HttpRequestMessage GetRequest(string path)
        {
            return GetRequest(path, token);
        }

        public static async Task<string> SetTaskComplete(string questId)
        {
            var request = GetRequest($"/progress/task/{questId}");
            request.Method = HttpMethod.Post;
            var payload = @$"{{""status"":""completed""";
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            Debug.WriteLine(await request.Content.ReadAsStringAsync());
            Debug.WriteLine(request?.RequestUri?.ToString());
            HttpResponseMessage response = await client.SendAsync(request);
            try
            {
                response.EnsureSuccessStatusCode();
                return "success";
            }
            catch (Exception ex)
            {
                var code = ((int)response.StatusCode).ToString();
                throw new Exception($"Invalid response code ({code}): {ex.Message}");
            }
        }

        public static async Task<TokenResponse> TestTokenAsync(string apiToken)
        {
            var path = "/token";
            var request = GetRequest(path, apiToken);
            HttpResponseMessage response = client.Send(request);
            try
            {
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                var code = ((int)response.StatusCode).ToString();
                if (code == "401")
                {

                }
                throw new Exception($"Invalid response code ({code}): {ex.Message}");
            }
            string responseBody = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<TokenResponse>(responseBody);
        }

        public static void SetToken(string apiToken)
        {
            token = apiToken;
        }

        public class TokenResponse
        {
            //public Dictionary<string, int> CreatedAt { get; set; }
            public string[] permissions { get; set; }
            public string token { get; set; }
            //public int Calls { get; set; }
        }
    }
}
