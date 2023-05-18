using System.Text;
using System.Text.Json;
using MudBlazor;

namespace TarkovMonitor
{
    internal class TarkovTracker
    {
        private static readonly string apiUrl = "https://tarkovtracker.io/api/v2";
        private static readonly HttpClient client = new();

        public static ProgressResponse Progress { get; private set; }
        public static bool ValidToken { get; private set; } = false;

        public static event EventHandler<EventArgs> TokenValidated;
        public static event EventHandler<EventArgs> TokenInvalid;
        public static event EventHandler<EventArgs> ProgressRetrieved;

        private static HttpRequestMessage GetRequest(string path, string apiToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, apiUrl + path);
            request.Headers.Add("Authorization", "Bearer " + apiToken);
            return request;
        }

        private static HttpRequestMessage GetRequest(string path)
        {
            return GetRequest(path, Properties.Settings.Default.tarkovTrackerToken);
        }

        public static async Task<string> SetTaskComplete(string questId)
        {
            if (!ValidToken)
            {
                return "invalid token";
            }
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
            }
            catch (Exception ex)
            {
                var code = ((int)response.StatusCode).ToString();
                if (code == "401")
                {
                    InvalidTokenException();
                }
                throw new Exception($"Invalid response code ({code}): {ex.Message}");
            }
            try
            {
                TarkovDev.Tasks.ForEach(async task => {
                    foreach (var failCondition in task.failConditions)
                    {
                        if (failCondition.task == null)
                        {
                            continue;
                        }
                        if (failCondition.task.id == questId && failCondition.status.Contains("complete"))
                        {
                            foreach (var taskStatus in Progress.data.tasksProgress)
                            {
                                if (taskStatus.id == failCondition.task.id)
                                {
                                    taskStatus.failed = true;
                                    break;
                                }
                            }
                            break;
                        }
                    }
                });
            } 
            catch (Exception ex)
            {
                // do something?
            }
            return "success";
        }

        public static async Task<string> SetTaskFailed(string questId)
        {
            if (!ValidToken)
            {
                return "invalid token";
            }
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
                foreach (var taskStatus in Progress.data.tasksProgress)
                {
                    if (taskStatus.id == questId)
                    {
                        taskStatus.failed = true;
                        break;
                    }
                }
                return "success";
            }
            catch (Exception ex)
            {
                var code = ((int)response.StatusCode).ToString();
                if (code == "401")
                {
                    InvalidTokenException();
                }
                throw new Exception($"Invalid response code ({code}): {ex.Message}");
            }
        }

        public static async Task<string> SetTaskUncomplete(string questId)
        {
            if (!ValidToken)
            {
                return "invalid token";
            }

            var updateNeeded = false;
            foreach (var taskStatus in Progress.data.tasksProgress)
            {
                if (taskStatus.id == questId)
                {
                    if (taskStatus.failed)
                    {
                        taskStatus.failed = false;
                        updateNeeded = true;
                    }
                    break;
                }
            }
            if (!updateNeeded)
            {
                return "task not marked as failed";
            }
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
                if (code == "401")
                {
                    InvalidTokenException();
                }
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
                    InvalidTokenException();
                }
                throw new Exception($"Invalid response code ({code}): {ex.Message}");
            }
            string responseBody = await response.Content.ReadAsStringAsync();
            Progress = JsonSerializer.Deserialize<ProgressResponse>(responseBody);
            ValidToken = true;
            ProgressRetrieved?.Invoke(null, new EventArgs());
            return Progress;
        }

        public static async Task<TokenResponse> TestToken(string apiToken)
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
                    InvalidTokenException();
                }
                throw new Exception($"Invalid response code ({code}): {ex.Message}");
            }
            string responseBody = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(responseBody);
            if (tokenResponse.permissions.Contains("WP"))
            {
                GetProgress();
                ValidToken = true;
                TokenValidated?.Invoke(null, new EventArgs());
            }
            else
            {
                Progress = new();
                ValidToken = false;
                TokenInvalid?.Invoke(null, new EventArgs());
            }
            return tokenResponse;
        }

        private static async void InvalidTokenException()
        {
            Progress = new();
            ValidToken = false;
            TokenInvalid?.Invoke(null, new EventArgs());
            throw new Exception("Tarkov Tracker token is invalid");
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
