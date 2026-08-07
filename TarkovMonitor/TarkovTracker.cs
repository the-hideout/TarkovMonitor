using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Transactions;
using Refit;

// TO DO: Implement rate limit policy of 15 requests per minute

namespace TarkovMonitor
{
    internal class TarkovTracker
    {
        internal interface ITarkovTrackerAPI
        {
            HttpClient Client { get; }

            [Get("/progress")]
            [Headers("Authorization: Bearer")]
            Task<ProgressResponse> GetProgress();

            [Post("/progress/task/{id}")]
            [Headers("Authorization: Bearer")]
            Task<string> SetTaskStatus(string id, [Body] TaskStatusBody body);

            [Post("/progress/tasks")]
            [Headers("Authorization: Bearer")]
            Task<string> SetTaskStatuses([Body] List<TaskStatusBody> body);
        }

        private static readonly HttpClient tokenInspectionClient = new();
        private static ITarkovTrackerAPI api = InitAPI();

        public static ProgressResponse Progress { get; private set; } = new();
        public static bool ValidToken { get; private set; } = false;
        public static bool IsLegacyService => string.Equals(
            Properties.Settings.Default.tarkovTrackerDomain,
            "tarkovtracker.io",
            StringComparison.OrdinalIgnoreCase);
        private static Dictionary<string, string> tokens = new();
        private static string currentProfile = "";
        public static string CurrentProfileId { get { return currentProfile; } }

        public static event EventHandler<EventArgs>? TokenValidated;
        public static event EventHandler<EventArgs>? TokenInvalid;
        public static event EventHandler<EventArgs>? ProgressRetrieved;
        public static Dictionary<string, string> Domains = new() {
            { "tarkovtracker.io", "TarkovTracker.io" },
            { "tarkovtracker.org", "TarkovTracker.org" },
        };

        static TarkovTracker() {
            tokenInspectionClient.DefaultRequestHeaders.UserAgent.TryParseAdd($"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name} {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
            tokens = JsonSerializer.Deserialize<Dictionary<string, string>>(Properties.Settings.Default.tarkovTrackerTokens) ?? tokens;
        }

        public static string GetApiBaseUrl(string trackerDomain)
        {
            if (trackerDomain == "tarkovtracker.org")
            {
                return "https://api.tarkovtracker.org";
            }
            return $"https://{trackerDomain}/api/v2";
        }

        public static bool IsSupportedOrgToken(string? token)
        {
            var value = token?.Trim() ?? string.Empty;
            if (value.Length != 22 || value[3] != '_')
            {
                return false;
            }

            var prefix = value[..3].ToUpperInvariant();
            if (prefix is not ("PVE" or "PVP" or "SZN"))
            {
                return false;
            }

            for (var index = 4; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9')
                    || (character >= 'a' && character <= 'f')
                    || (character >= 'A' && character <= 'F')))
                {
                    return false;
                }
            }

            return true;
        }

        private static string GetOrgTokenPrefix(string token)
        {
            var value = token.Trim();
            return value[..3].ToUpperInvariant();
        }

        private static void VerifyOrgTokenResponse(string submittedToken, TokenResponse response)
        {
            var submitted = submittedToken.Trim();
            if (!IsSupportedOrgToken(submitted))
            {
                throw new Exception("The TarkovTracker.org API key format is invalid.");
            }

            var returnedToken = response.token?.Trim();
            if (!string.IsNullOrWhiteSpace(returnedToken))
            {
                if (!IsSupportedOrgToken(returnedToken)
                    || !string.Equals(GetOrgTokenPrefix(submitted), GetOrgTokenPrefix(returnedToken), StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(submitted[4..], returnedToken[4..], StringComparison.Ordinal))
                {
                    throw new Exception("TarkovTracker returned a different API key than the one supplied.");
                }
            }

            if (string.IsNullOrWhiteSpace(response.gameMode))
            {
                return;
            }

            var verifiedPrefix = response.gameMode.Trim().ToLowerInvariant() switch
            {
                "pve" => "PVE",
                "pvp" or "regular" => "PVP",
                "seasonal" or "pvpseason" or "sn1" => "SZN",
                _ => throw new Exception("TarkovTracker returned an unsupported game mode for this API key."),
            };
            var submittedPrefix = GetOrgTokenPrefix(submitted);
            if (!string.Equals(submittedPrefix, verifiedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception($"This API key is {submittedPrefix}, but TarkovTracker verified it as {verifiedPrefix}.");
            }
        }

        public static ITarkovTrackerAPI InitAPI()
        {
            api = RestService.For<ITarkovTrackerAPI>(GetApiBaseUrl(Properties.Settings.Default.tarkovTrackerDomain),
                new RefitSettings {
                    AuthorizationHeaderValueGetter = (rq, cr) => {
                        return new ValueTask<string>(Task.Run<string>(() => {
                            return GetToken(currentProfile ?? "");
                        }));
                    },
                }
            );
            api.Client.DefaultRequestHeaders.UserAgent.TryParseAdd($"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name} {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
            return api;
        }

        public static void ResetActiveState()
        {
            currentProfile = "";
            ValidToken = false;
            Progress = new();
        }

        public static string GetToken(string profileId)
        {
            if (!tokens.ContainsKey(profileId))
            {
                return "";
            }
            return tokens[profileId];
        }

        public static void SetToken(string profileId, string token)
        {
            if (IsLegacyService)
            {
                return;
            }
            if (profileId == "")
            {
                throw new Exception("No PVP or PVE profile initialized, please launch Escape from Tarkov first");
            }
            tokens[profileId] = token;
            Properties.Settings.Default.tarkovTrackerTokens = JsonSerializer.Serialize(tokens);
            Properties.Settings.Default.Save();
        }

        public static async Task<ProgressResponse> SetProfile(string profileId)
        {
            if (IsLegacyService)
            {
                ResetActiveState();
                return Progress;
            }
            if (profileId == "") {
                throw new Exception("Can't set PVP or PVE profile, please launch Escape from Tarkov and then restart this application");
            }

            if (currentProfile == profileId)
            {
                return Progress;
            }
            var newToken = GetToken(profileId);
            var oldToken = GetToken(currentProfile);
            currentProfile = profileId;
            if (oldToken == newToken)
            {
                return Progress;
            }
            if (newToken == "" || newToken.Length != 22)
            {
                ValidToken = false;
                Progress = new();
                return Progress;
            }
            await TestToken(newToken);
            return Progress;
        }

        private static void SyncStoredStatus(string questId, TaskStatus status)
        {
            var storedStatus = Progress.data.tasksProgress.Find(ts => ts.id == questId);
            if (storedStatus == null)
            {
                storedStatus = new()
                {
                    id = questId,
                };
                Progress.data.tasksProgress.Add(storedStatus);
            }
            if (status == TaskStatus.Finished && !storedStatus.complete)
            {
                storedStatus.complete = true;
                storedStatus.failed = false;
                storedStatus.invalid = false;
            }
            if (status == TaskStatus.Failed && !storedStatus.failed)
            {
                storedStatus.complete = false;
                storedStatus.failed = true;
                storedStatus.invalid = false;
            }
            if (status == TaskStatus.Started && (storedStatus.failed || storedStatus.invalid || storedStatus.complete))
            {
                storedStatus.complete = false;
                storedStatus.failed = false;
                storedStatus.invalid = false;
            }
        }

        public static async Task<string> SetTaskStatus(string questId, TaskStatus status)
        {
            if (IsLegacyService || !ValidToken)
            {
                throw new Exception("Invalid token");
            }
            try
            {
                await api.SetTaskStatus(questId, TaskStatusBody.From(status));
                SyncStoredStatus(questId, status);
            }
            catch (ApiException ex)
            {
                if (ex.StatusCode == HttpStatusCode.Unauthorized)
                {
                    InvalidTokenException();
                }
                if (ex.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new Exception("Rate limited by Tarkov Tracker API");
                }
                throw new Exception($"Invalid TarkovTracker API response code: {ex.Message}");
            }
            catch (Exception ex)
            {
                throw new Exception($"TarkovTracker API error: {ex.Message}");
            }
            return "success";
        }

        public static async Task<string> SetTaskComplete(string questId)
        {
            await SetTaskStatus(questId, TaskStatus.Finished);
            try
            {
                TarkovDev.Tasks.ForEach(task => {
                    foreach (var failCondition in task.failConditions)
                    {
                        if (failCondition.task == null)
                        {
                            continue;
                        }
                        if (failCondition.task == questId && failCondition.status.Contains("complete"))
                        {
                            foreach (var taskStatus in Progress.data.tasksProgress)
                            {
                                if (taskStatus.id == failCondition.task)
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
            catch (Exception)
            {
                // do something?
            }
            return "success";
        }

        public static async Task<string> SetTaskFailed(string questId)
        {
            return await SetTaskStatus(questId, TaskStatus.Failed);
        }

        public static async Task<string> SetTaskStarted(string questId)
        {
            foreach (var taskStatus in Progress.data.tasksProgress)
            {
                if (taskStatus.id != questId)
                {
                    continue;
                }
                if (taskStatus.failed)
                {
                    return await SetTaskStatus(questId, TaskStatus.Started);
                }
                break;
            }
            return "task not marked as failed";
        }

        public static async Task<string> SetTaskStatuses(Dictionary<string, TaskStatus> statuses)
        {
			if (IsLegacyService || !ValidToken)
			{
				throw new Exception("Invalid token");
			}
            List<TaskStatusBody> body = new();
            foreach (var kvp in statuses)
            {
                TaskStatusBody status = TaskStatusBody.From(kvp.Value);
                status.id = kvp.Key;
                body.Add(status);
            }
			try
			{
				await api.SetTaskStatuses(body);
                foreach( var kvp in statuses)
                {
                    SyncStoredStatus(kvp.Key, kvp.Value);
                }
			}
			catch (ApiException ex)
			{
				if (ex.StatusCode == HttpStatusCode.Unauthorized)
				{
					InvalidTokenException();
                }
                if (ex.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new Exception("Rate limited by Tarkov Tracker API");
                }
                throw new Exception($"Invalid TarkovTracker API response code: {ex.Message}");
			}
			catch (Exception ex)
			{
				throw new Exception($"TarkovTracker API error: {ex.Message}");
			}
			return "success";
		}

        public static async Task<ProgressResponse> GetProgress()
		{
			if (IsLegacyService)
			{
				ResetActiveState();
				return Progress;
			}
			if (!ValidToken)
			{
				throw new Exception("Invalid token");
			}
            try
            {
                Progress = await api.GetProgress();
                ProgressRetrieved?.Invoke(null, new EventArgs());
                return Progress;
            }
            catch (ApiException ex)
            {
                if (ex.StatusCode == HttpStatusCode.Unauthorized)
                {
                    InvalidTokenException();
                }
                if (ex.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new Exception("Rate limited by Tarkov Tracker API");
                }
                throw new Exception($"Invalid TarkovTracker response code: {ex.Message}");
            }
            catch (Exception ex)
            {
                throw new Exception($"TarkovTracker API error: {ex.Message}");
            }
        }

        public static async Task<TokenResponse> TestToken(string apiToken)
        {
            if (IsLegacyService)
            {
                throw new InvalidOperationException(
                    "Support for TarkovTracker.io has been retired. Switch to TarkovTracker.org.");
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{GetApiBaseUrl(Properties.Settings.Default.tarkovTrackerDomain).TrimEnd('/')}/token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());

            HttpResponseMessage httpResponse;
            try
            {
                httpResponse = await tokenInspectionClient.SendAsync(request);
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"TarkovTracker API connection error: {ex.Message}");
            }

            using (httpResponse)
            {
                if (httpResponse.StatusCode == HttpStatusCode.Unauthorized)
                {
                    InvalidTokenException();
                }
                if (httpResponse.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new Exception("Rate limited by Tarkov Tracker API");
                }
                if (!httpResponse.IsSuccessStatusCode)
                {
                    throw new Exception($"Invalid TarkovTracker API response code: {(int)httpResponse.StatusCode} {httpResponse.ReasonPhrase}");
                }

                var responseBody = await httpResponse.Content.ReadAsStringAsync();
                var response = JsonSerializer.Deserialize<TokenResponse>(responseBody)
                    ?? throw new Exception("TarkovTracker returned an empty token response.");
                VerifyOrgTokenResponse(apiToken, response);
                if (response.permissions.Contains("WP"))
                {
                    ValidToken = true;
                    await GetProgress();
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
        }

        private static void InvalidTokenException()
        {
            Progress = new();
            ValidToken = false;
            TokenInvalid?.Invoke(null, new EventArgs());
            throw new Exception("Tarkov Tracker API token is invalid");
        }

        public static bool HasAirFilter()
        {
            if (Progress == null)
            {
                return false;
            }
            var airFilterStation = TarkovDev.Stations.Find(s => s.normalizedName == "air-filtering-unit");
            if (airFilterStation == null)
            {
                return false;
            }
            var stationLevel = airFilterStation.levels.FirstOrDefault();
            if (stationLevel == null)
            {
                return false;
            }
            var built = Progress.data.hideoutModulesProgress.Find(m => m.id == stationLevel.id && m.complete);
            return built != null;
        }

        public class TokenResponse
        {
            public bool success { get; set; }
            public List<string> permissions { get; set; } = new();
            public string? token { get; set; }
            public string? gameMode { get; set; }
        }

        public class ProgressResponse
        {
            public ProgressResponseData data { get; set; } = new();
            public ProgressResponseMeta meta { get; set; } = new();
        }

        public class ProgressResponseData
        {
            public List<ProgressResponseTask> tasksProgress { get; set; } = new();
            public List<ProgressResponseHideoutModules> hideoutModulesProgress { get; set; } = new();
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
        public class ProgressResponseHideoutModules    
        {
            public string id { get; set; }
            public bool complete { get; set; }
        }
        public class ProgressResponseMeta
        {
            public string self { get; set; }
        }
        public class TaskStatusBody
        {
            public string? id { get; set; }
            public string state { get; private set; }
            private TaskStatusBody(string newState)
            {
                state = newState;
            }
            public static TaskStatusBody Completed => new("completed");
            public static TaskStatusBody Uncompleted => new("uncompleted");
            public static TaskStatusBody Failed => new("failed");
            public static TaskStatusBody From(TaskStatus code)
            {
                if (code == TaskStatus.Finished)
                {
                    return TaskStatusBody.Completed;
                }
                if (code == TaskStatus.Failed)
                {
                    return TaskStatusBody.Failed;
                }
                return TaskStatusBody.Uncompleted;
            }
            public static TaskStatusBody From(MessageType messageType)
            {
                return TaskStatusBody.From((TaskStatus)messageType);
            }
        }
    }
}
