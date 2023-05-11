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
            var payload = @$"{{""state"":""completed""}}";
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            //Debug.WriteLine(await request.Content.ReadAsStringAsync());
            //Debug.WriteLine(request?.RequestUri?.ToString());
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

        public static async Task<string> SetTaskFailed(string questId)
        {
            var request = GetRequest($"/progress/task/{questId}");
            request.Method = HttpMethod.Post;
            var payload = @$"{{""state"":""failed""}}";
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            //Debug.WriteLine(await request.Content.ReadAsStringAsync());
            //Debug.WriteLine(request?.RequestUri?.ToString());
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

        public static async Task<string> SetTaskUncomplete(string questId)
        {
            var request = GetRequest($"/progress/task/{questId}");
            request.Method = HttpMethod.Post;
            var payload = @$"{{""state"":""uncompleted""}}";
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            //Debug.WriteLine(await request.Content.ReadAsStringAsync());
            //Debug.WriteLine(request?.RequestUri?.ToString());
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

        public static async Task<ProgressResponse> GetProgress()
        {
            var request = GetRequest("/progress");
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
            return JsonSerializer.Deserialize<ProgressResponse>(responseBody);
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

        public class ProgressResponse
        {
            public ProgressResponseData data { get; set; }
            public ProgressResponseMeta meta { get; set; }
        }

        public class ProgressResponseData
        {
            public ProgressResponseTask[] tasksProgress { get; set; }
            public ProgressResponseHideoutPart[] hideoutModulesProgress { get; set; }
            public string? displayName { get; set; }
            public string userId { get; set; }
            public int playerLevel { get; set; }
            public int gameEdition { get; set; }
            public string pmcFaction { get; set; }
        }

        public class ProgressResponseTask
        {
            public string id { get; set; }
            public bool complete { get; set; }
            public bool invalid { get; set; }
            public bool failed { get; set; }
        }
        public class ProgressResponseHideoutPart    
        {
            public string id { get; set; }
            public bool complete { get; set; }
            public int count { get; set; }
        }
        public class ProgressResponseMeta
        {
            public string self { get; set; }
        }
    }
}
