using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Transactions;
using Refit;

// TO DO: Implement rate limit policy of 15 requests per minute

namespace TarkovMonitor
{
    internal sealed class TrackerActiveStateCompatibilityException : Exception
    {
        public TrackerActiveStateCompatibilityException(HttpStatusCode statusCode, Exception innerException)
            : base(
                $"TarkovTracker rejected explicit task state 'active' ({(int)statusCode} {statusCode}). " +
                "Accepted-task sync requires a Tracker server version that supports active task state. " +
                "TarkovMonitor did not retry the update as 'uncompleted'.",
                innerException)
        {
        }
    }

    internal class TarkovTracker
    {
        internal interface ITarkovTrackerAPI
        {
            HttpClient Client { get; }

            [Get("/progress")]
            Task<ProgressResponse> GetProgress([Header("Authorization")] string authorization);

            [Post("/progress/task/{id}")]
            Task<string> SetTaskStatus(
                string id,
                [Body] TaskStatusBody body,
                [Header("Authorization")] string authorization);

            [Post("/progress/tasks")]
            Task<string> SetTaskStatuses(
                [Body] List<TaskStatusBody> body,
                [Header("Authorization")] string authorization);
        }

        private static readonly HttpClient tokenInspectionClient = new();
        private static readonly TrackerAuthorizationState<ProgressResponse> authorizationState = new(() => new());
        private static readonly Dictionary<string, string> tokens =
            JsonSerializer.Deserialize<Dictionary<string, string>>(
                Properties.Settings.Default.tarkovTrackerTokens) ?? new();
        private static readonly TrackerProfileTokenState<ProgressResponse> profileTokenState =
            new(authorizationState, tokens);
        private static readonly TrackerEndpointState<ITarkovTrackerAPI> endpointState = CreateInitialEndpoint();

        public static ProgressResponse Progress => authorizationState.Progress;
        public static bool ValidToken => authorizationState.Valid;
        public static bool IsLegacyService => string.Equals(
            Properties.Settings.Default.tarkovTrackerDomain,
            "tarkovtracker.io",
            StringComparison.OrdinalIgnoreCase);
        public static string CurrentProfileId => authorizationState.CurrentProfileId;

        public static event EventHandler<EventArgs>? TokenValidated;
        public static event EventHandler<EventArgs>? TokenInvalid;
        public static event EventHandler<EventArgs>? ProgressRetrieved;
        public static Dictionary<string, string> Domains = new() {
            { "tarkovtracker.io", "TarkovTracker.io" },
            { "tarkovtracker.org", "TarkovTracker.org" },
        };

        static TarkovTracker() {
            tokenInspectionClient.DefaultRequestHeaders.UserAgent.TryParseAdd($"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name} {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
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
            return TrackerTokenFormat.IsSupportedOrgToken(token);
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

        private static TrackerEndpointState<ITarkovTrackerAPI> CreateInitialEndpoint()
        {
            var domain = Properties.Settings.Default.tarkovTrackerDomain;
            var baseUrl = GetApiBaseUrl(domain);
            return new(baseUrl, IsOrgDomain(domain), CreateApiClient(baseUrl));
        }

        private static ITarkovTrackerAPI CreateApiClient(string baseUrl)
        {
            var client = RestService.For<ITarkovTrackerAPI>(baseUrl);
            client.Client.DefaultRequestHeaders.UserAgent.TryParseAdd($"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name} {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
            return client;
        }

        private static bool IsOrgDomain(string? domain) =>
            string.Equals(domain, "tarkovtracker.org", StringComparison.OrdinalIgnoreCase);

        public static ITarkovTrackerAPI InitAPI()
        {
            var domain = Properties.Settings.Default.tarkovTrackerDomain;
            var baseUrl = GetApiBaseUrl(domain);
            var endpoint = endpointState.Replace(baseUrl, IsOrgDomain(domain), CreateApiClient(baseUrl));
            authorizationState.Reset();
            return endpoint.Client;
        }

        public static void ResetActiveState()
        {
            authorizationState.Reset();
        }

        public static string GetToken(string profileId)
        {
            return profileTokenState.GetToken(profileId);
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
            var tokensSnapshot = profileTokenState.ReplaceToken(profileId, token);
            Properties.Settings.Default.tarkovTrackerTokens = JsonSerializer.Serialize(tokensSnapshot);
            Properties.Settings.Default.Save();
        }

        public static async Task<ProgressResponse> SetProfile(Profile profile)
        {
            if (IsLegacyService)
            {
                ResetActiveState();
                return Progress;
            }
            if (authorizationState.TryAuthorize(profile, out _))
            {
                return Progress;
            }

            var profileSnapshot = profile.Snapshot();
            var endpoint = endpointState.Snapshot;
            var (newToken, profileSwitch) = profileTokenState.BeginSwitch(
                profileSnapshot,
                endpoint.Generation);
            if (profileSnapshot.Id == "" || profileSnapshot.Type == ProfileType.Unknown)
            {
                throw new Exception("Can't set PVP or PVE profile, please launch Escape from Tarkov and then restart this application");
            }
            if (!endpoint.IsOrg || !TrackerTokenFormat.MatchesMode(newToken, profileSnapshot.Type))
            {
                return Progress;
            }

            var tokenResponse = await TestToken(newToken, endpoint);
            if (!authorizationState.IsCurrent(profileSwitch))
            {
                return Progress;
            }
            if (!tokenResponse.permissions.Contains("WP"))
            {
                TokenInvalid?.Invoke(null, EventArgs.Empty);
                return Progress;
            }

            var loadedProgress = await GetProgress(newToken, endpoint);
            if (authorizationState.TryActivate(profileSwitch, loadedProgress, out _))
            {
                ProgressRetrieved?.Invoke(null, EventArgs.Empty);
                TokenValidated?.Invoke(null, EventArgs.Empty);
            }
            return Progress;
        }

        internal static void SyncStoredStatus(ProgressResponse progress, string questId, TaskStatus status)
        {
            var storedStatus = progress.data.tasksProgress.Find(ts => ts.id == questId);
            if (storedStatus == null)
            {
                storedStatus = new()
                {
                    id = questId,
                };
                progress.data.tasksProgress.Add(storedStatus);
            }
            TaskLifecycle.ApplyToCache(storedStatus, status);
        }

        public static bool TryAuthorizeWrite(
            Profile profile,
            out TrackerWriteAuthorization authorization) =>
            !IsLegacyService && authorizationState.TryAuthorize(profile, out authorization);

        public static async Task<string> SetTaskStatus(
            TrackerWriteAuthorization authorization,
            string questId,
            TaskStatus status)
        {
            if (!authorizationState.IsCurrent(authorization)
                || !endpointState.TryResolve(authorization, out var endpoint))
            {
                throw new InvalidOperationException(
                    "The TarkovTracker write authorization is no longer current for this profile and mode.");
            }
            try
            {
                await endpoint.Client.SetTaskStatus(
                    questId,
                    TaskStatusBody.From(status),
                    authorization.AuthorizationHeader);
                authorizationState.UpdateIfCurrent(
                    authorization,
                    progress => SyncStoredStatus(progress, questId, status));
            }
            catch (ApiException ex)
            {
                if (ex.StatusCode == HttpStatusCode.Unauthorized)
                {
                    InvalidateAuthorization(authorization);
                }
                if (ex.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new Exception("Rate limited by Tarkov Tracker API");
                }
                if (status == TaskStatus.Started
                    && TrackerCompatibility.IsUnsupportedActiveState(ex.StatusCode, ex.Content))
                {
                    throw new TrackerActiveStateCompatibilityException(ex.StatusCode, ex);
                }
                throw new Exception($"Invalid TarkovTracker API response code: {ex.StatusCode}.", ex);
            }
            catch (Exception ex)
            {
                throw new Exception("TarkovTracker API error.", ex);
            }
            return "success";
        }

        public static async Task<string> SetTaskComplete(
            TrackerWriteAuthorization authorization,
            string questId)
        {
            await SetTaskStatus(authorization, questId, TaskStatus.Finished);
            try
            {
                authorizationState.UpdateIfCurrent(authorization, progress =>
                {
                    TarkovDev.Tasks.ForEach(task =>
                    {
                        foreach (var failCondition in task.failConditions)
                        {
                            if (failCondition.task == null)
                            {
                                continue;
                            }
                            if (failCondition.task == questId && failCondition.status?.Contains("complete") == true)
                            {
                                foreach (var taskStatus in progress.data.tasksProgress)
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
                });
            } 
            catch (Exception ex)
            {
                throw new Exception("TarkovTracker local task state update failed.", ex);
            }
            return "success";
        }

        public static async Task<string> SetTaskFailed(
            TrackerWriteAuthorization authorization,
            string questId)
        {
            return await SetTaskStatus(authorization, questId, TaskStatus.Failed);
        }

        public static async Task<string> SetTaskStarted(
            TrackerWriteAuthorization authorization,
            string questId)
        {
            return await SetTaskStatus(authorization, questId, TaskStatus.Started);
        }

        public static async Task<string> SetTaskStatuses(
            TrackerWriteAuthorization authorization,
            Dictionary<string, TaskStatus> statuses)
        {
			if (!authorizationState.IsCurrent(authorization)
                || !endpointState.TryResolve(authorization, out var endpoint))
			{
				throw new InvalidOperationException(
                    "The TarkovTracker write authorization is no longer current for this profile and mode.");
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
				await endpoint.Client.SetTaskStatuses(body, authorization.AuthorizationHeader);
                authorizationState.UpdateIfCurrent(authorization, progress =>
                {
                    foreach (var kvp in statuses)
                    {
                        SyncStoredStatus(progress, kvp.Key, kvp.Value);
                    }
                });
			}
			catch (ApiException ex)
			{
				if (ex.StatusCode == HttpStatusCode.Unauthorized)
				{
					InvalidateAuthorization(authorization);
                }
                if (ex.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new Exception("Rate limited by Tarkov Tracker API");
                }
				if (statuses.Values.Contains(TaskStatus.Started)
                    && TrackerCompatibility.IsUnsupportedActiveState(ex.StatusCode, ex.Content))
				{
					throw new TrackerActiveStateCompatibilityException(ex.StatusCode, ex);
				}
				throw new Exception($"Invalid TarkovTracker API response code: {ex.StatusCode}.", ex);
			}
			catch (Exception ex)
			{
				throw new Exception("TarkovTracker API error.", ex);
			}
			return "success";
		}

        private static async Task<ProgressResponse> GetProgress(
            string token,
            TrackerEndpointSnapshot<ITarkovTrackerAPI> endpoint)
		{
            try
            {
                return await endpoint.Client.GetProgress($"Bearer {token}");
            }
            catch (ApiException ex)
            {
                if (ex.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new Exception("Tarkov Tracker API token is invalid", ex);
                }
                if (ex.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new Exception("Rate limited by Tarkov Tracker API");
                }
                throw new Exception($"Invalid TarkovTracker response code: {ex.StatusCode}.", ex);
            }
            catch (Exception ex)
            {
                throw new Exception("TarkovTracker API error.", ex);
            }
        }

        public static async Task<TokenResponse> TestToken(string apiToken)
        {
            return await TestToken(apiToken, endpointState.Snapshot);
        }

        private static async Task<TokenResponse> TestToken(
            string apiToken,
            TrackerEndpointSnapshot<ITarkovTrackerAPI> endpoint)
        {
            if (!endpoint.IsOrg)
            {
                throw new InvalidOperationException(
                    "Support for TarkovTracker.io has been retired. Switch to TarkovTracker.org.");
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{endpoint.BaseUrl.TrimEnd('/')}/token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());

            HttpResponseMessage httpResponse;
            try
            {
                httpResponse = await tokenInspectionClient.SendAsync(request);
            }
            catch (HttpRequestException ex)
            {
                throw new Exception("TarkovTracker API connection error.", ex);
            }

            using (httpResponse)
            {
                if (httpResponse.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new Exception("Tarkov Tracker API token is invalid");
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
                return response;
            }
        }

        private static void InvalidateAuthorization(TrackerWriteAuthorization authorization)
        {
            if (authorizationState.InvalidateIfCurrent(authorization))
            {
                TokenInvalid?.Invoke(null, EventArgs.Empty);
            }
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
            public List<TrackerTaskProgress> tasksProgress { get; set; } = new();
            public List<ProgressResponseHideoutModules> hideoutModulesProgress { get; set; } = new();
            public string? displayName { get; set; }
            public string userId { get; set; }
            public int playerLevel { get; set; }
            public int gameEdition { get; set; }
            public string pmcFaction { get; set; }
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
            public static TaskStatusBody Active => new("active");
            public static TaskStatusBody From(TaskStatus code)
            {
                return new TaskStatusBody(TaskLifecycle.ToTrackerState(code));
            }
            public static TaskStatusBody From(MessageType messageType)
            {
                return TaskStatusBody.From((TaskStatus)messageType);
            }
        }
    }
}
