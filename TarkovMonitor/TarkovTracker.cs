using Refit;
using static TarkovMonitor.TarkovTracker;

// TO DO: Implement rate limit policy of 15 requests per minute

namespace TarkovMonitor
{
    internal interface ITarkovTrackerAPI
    {
        [Get("/progress")]
        Task<ProgressResponse> GetProgress([Header("Authorization")] string authorizationHeaderValue);
        [Get("/token")]
        Task<TokenResponse> TestToken([Header("Authorization")] string authorizationHeaderValue);
        [Post("/progress/task/{id}")]
        Task<string> SetTaskComplete(string id, [Header("Authorization")] string authorizationHeaderValue, [Body] string body = @$"{{""state"":""completed""}}");
        [Post("/progress/task/{id}")]
        Task<string> SetTaskFailed(string id, [Header("Authorization")] string authorizationHeaderValue, [Body] string body = @$"{{""state"":""failed""}}");
        [Post("/progress/task/{id}")]
        Task<string> SetTaskUncomplete(string id, [Header("Authorization")] string authorizationHeaderValue, [Body] string body = @$"{{""state"":""uncompleted""}}");
    }

    internal class TarkovTracker
    {
        private static ITarkovTrackerAPI api = RestService.For<ITarkovTrackerAPI>("https://tarkovtracker.io/api/v2");

        public static ProgressResponse Progress { get; private set; }
        public static bool ValidToken { get; private set; } = false;

        public static event EventHandler<EventArgs> TokenValidated;
        public static event EventHandler<EventArgs> TokenInvalid;
        public static event EventHandler<EventArgs> ProgressRetrieved;
        public static async Task<string> SetTaskComplete(string questId)
        {
            if (!ValidToken)
            {
				throw new Exception("Invalid token");
			}
            try
            {
                await api.SetTaskComplete(questId, $"Bearer {Properties.Settings.Default.tarkovTrackerToken}");
            }
            catch (ApiException ex)
            {
                if (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    InvalidTokenException();
                }
                throw new Exception($"Invalid response code ({ex.StatusCode}): {ex.Message}");
            }
            catch (Exception ex)
            {
                throw new Exception($"TarkovTracker API error: {ex.Message}");
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
				throw new Exception("Invalid token");
			}
            try
            {
                await api.SetTaskFailed(questId, $"Bearer {Properties.Settings.Default.tarkovTrackerToken}");
                return "success";
            }
            catch (ApiException ex)
            {
                if (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    InvalidTokenException();
                }
                throw new Exception($"Invalid response code ({ex.StatusCode}): {ex.Message}");
            }
            catch (Exception ex)
            {
                throw new Exception($"TarkovTracker API error: {ex.Message}");
            }
        }

        public static async Task<string> SetTaskUncomplete(string questId)
        {
            if (!ValidToken)
            {
                throw new Exception("Invalid token");
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
            try
            {
                await api.SetTaskUncomplete(questId, $"Bearer {Properties.Settings.Default.tarkovTrackerToken}");
                return "success";
            }
            catch (ApiException ex)
            {
                if (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    InvalidTokenException();
                }
                throw new Exception($"Invalid response code ({ex.StatusCode}): {ex.Message}");
            }
            catch (Exception ex)
            {
                throw new Exception($"TarkovTracker API error: {ex.Message}");
            }
        }

        public static async Task<ProgressResponse> GetProgress()
		{
			if (!ValidToken)
			{
				throw new Exception("Invalid token");
			}
            try
            {
                Progress = await api.GetProgress($"Bearer {Properties.Settings.Default.tarkovTrackerToken}");
                ProgressRetrieved?.Invoke(null, new EventArgs());
                return Progress;
            }
            catch (ApiException ex)
            {
                if (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    InvalidTokenException();
                }
                throw new Exception($"Invalid response code ({ex.StatusCode}): {ex.Message}");
            }
            catch (Exception ex)
            {
                throw new Exception($"TarkovTracker API error: {ex.Message}");
            }
        }

        public static async Task<TokenResponse> TestToken(string apiToken)
        {
            try
            {
                var response = await api.TestToken($"Bearer {apiToken}");
                if (response.permissions.Contains("WP"))
                {
                    ValidToken = true;
                    GetProgress();
                    TokenValidated?.Invoke(null, new EventArgs());
                }
                else
                {
                    Progress = new();
                    ValidToken = false;
                    TokenInvalid?.Invoke(null, new EventArgs());
                }
                return response;
            }
            catch (ApiException ex)
            {
                if (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    InvalidTokenException();
                }
                throw new Exception($"Invalid response code ({ex.StatusCode}): {ex.Message}");
            }
            catch (Exception ex)
            {
                throw new Exception($"TarkovTracker API error: {ex.Message}");
            }
        }

        private static void InvalidTokenException()
        {
            Progress = new();
            ValidToken = false;
            TokenInvalid?.Invoke(null, new EventArgs());
            throw new Exception("Tarkov Tracker token is invalid");
        }

        public class TokenResponse
        {
            //public Dictionary<string, int> CreatedAt { get; set; }
            public List<string> permissions { get; set; }
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
            public List<ProgressResponseTask> tasksProgress { get; set; }
            public List<ProgressResponseHideoutPart> hideoutModulesProgress { get; set; }
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
