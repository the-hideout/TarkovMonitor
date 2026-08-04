using System.Net;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Text;
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

            [Get("/token")]
            Task<TokenResponse> TestToken([Header("Authorization")] string authorization);

            [Get("/progress")]
            Task<ProgressResponse> GetProgress([Header("Authorization")] string authorization);

            [Post("/progress/task/{id}")]
            Task<string> SetTaskStatus(string id, [Body] TaskStatusBody body, [Header("Authorization")] string authorization);

            [Post("/progress/tasks")]
            Task<string> SetTaskStatuses([Body] List<TaskStatusBody> body, [Header("Authorization")] string authorization);
        }

        private static readonly HttpClient tokenInspectionClient = new();
        private readonly record struct ActiveRequest(
            string ProfileId,
            EftSessionMode SessionMode,
            string Token,
            long Generation,
            ITarkovTrackerAPI Api);

        public sealed class ProgressRetrievedEventArgs : EventArgs
        {
            public string ProfileId { get; }
            public EftSessionMode SessionMode { get; }
            public ProgressResponse Progress { get; }

            public ProgressRetrievedEventArgs(string profileId, EftSessionMode sessionMode, ProgressResponse progress)
            {
                ProfileId = profileId;
                SessionMode = sessionMode;
                Progress = progress;
            }
        }

        private static readonly object stateLock = new();
        private static long activationGeneration;
        private static ITarkovTrackerAPI api = InitAPI();

        public static ProgressResponse Progress { get; private set; } = new();
        public static bool ValidToken { get; private set; } = false;
        // TarkovTracker.io compatibility store. Keys are EFT profile IDs.
        private static Dictionary<string, string> tokens = new();
        // TarkovTracker.org store. Keys are source-controlled API key prefixes.
        private static Dictionary<string, string> modeTokens = new(StringComparer.OrdinalIgnoreCase);
        // Fingerprints recorded here prove which exact .org tokens passed the /token endpoint.
        private static Dictionary<string, string> verifiedModeTokenHashes = new(StringComparer.OrdinalIgnoreCase);
        private static string currentProfile = "";
        private static EftSessionMode currentSessionMode = EftSessionMode.Unknown;
        private static string activeToken = "";
        private static readonly object importValidationLock = new();
        private static DateTimeOffset nextImportValidationAllowedAt = DateTimeOffset.MinValue;
        private static bool importValidationInProgress;
        private static readonly TimeSpan importValidationInterval = TimeSpan.FromMinutes(1);
        public static string CurrentProfileId
        {
            get
            {
                lock (stateLock)
                {
                    return currentProfile;
                }
            }
        }
        public static EftSessionMode CurrentSessionMode
        {
            get
            {
                lock (stateLock)
                {
                    return currentSessionMode;
                }
            }
        }
        public static bool IsLegacyService => Properties.Settings.Default.tarkovTrackerDomain == "tarkovtracker.io";

        public static event EventHandler<EventArgs>? TokenValidated;
        public static event EventHandler<EventArgs>? TokenInvalid;
        public static event EventHandler<ProgressRetrievedEventArgs>? ProgressRetrieved;
        public static Dictionary<string, string> Domains = new() {
            { "tarkovtracker.io", "TarkovTracker.io" },
            { "tarkovtracker.org", "TarkovTracker.org" },
        };

        static TarkovTracker() {
            tokenInspectionClient.DefaultRequestHeaders.UserAgent.TryParseAdd($"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name} {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
            tokens = JsonSerializer.Deserialize<Dictionary<string, string>>(Properties.Settings.Default.tarkovTrackerTokens) ?? tokens;
            var storedModeTokens = JsonSerializer.Deserialize<Dictionary<string, string>>(Properties.Settings.Default.tarkovTrackerModeTokens);
            if (storedModeTokens != null)
            {
                modeTokens = new Dictionary<string, string>(storedModeTokens, StringComparer.OrdinalIgnoreCase);
                MigrateModeTokenKeys();
                RemoveInvalidPrefixAssignments();
            }
            var storedVerifiedModeTokenHashes = JsonSerializer.Deserialize<Dictionary<string, string>>(Properties.Settings.Default.tarkovTrackerVerifiedModeTokenHashes);
            if (storedVerifiedModeTokenHashes != null)
            {
                verifiedModeTokenHashes = new Dictionary<string, string>(storedVerifiedModeTokenHashes, StringComparer.OrdinalIgnoreCase);
            }
            RemoveStaleVerificationRecords();
        }

        private static void RemoveStaleVerificationRecords()
        {
            var stalePrefixes = verifiedModeTokenHashes
                .Where(pair => !modeTokens.TryGetValue(pair.Key, out var storedToken)
                    || !string.Equals(pair.Value, ComputeTokenFingerprint(storedToken), StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToList();
            if (stalePrefixes.Count == 0)
            {
                return;
            }
            foreach (var prefix in stalePrefixes)
            {
                verifiedModeTokenHashes.Remove(prefix);
            }
            SaveVerifiedModeTokens();
        }

        public static IReadOnlyList<PendingTokenValidation> GetPendingTokenValidations()
        {
            var candidates = new List<string>();
            candidates.AddRange(modeTokens
                .Where(pair => !IsTokenVerified(pair.Key, pair.Value))
                .Select(pair => pair.Value));
            return candidates
                .Where(token => !string.IsNullOrWhiteSpace(token) && IsImportablePrefix(GetTokenPrefix(token)))
                .Select(token => token.Trim())
                .Distinct(StringComparer.Ordinal)
                .Select(token => new PendingTokenValidation(
                    token,
                    GetPrefixDisplayName(GetTokenPrefix(token))))
                .ToList();
        }

        private static bool IsTokenVerified(string prefix, string token)
        {
            return verifiedModeTokenHashes.TryGetValue(prefix, out var verifiedTokenHash)
                && string.Equals(verifiedTokenHash, ComputeTokenFingerprint(token), StringComparison.Ordinal);
        }

        private static string ComputeTokenFingerprint(string token)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));
        }

        private static void SaveVerifiedModeTokens()
        {
            Properties.Settings.Default.tarkovTrackerVerifiedModeTokenHashes = JsonSerializer.Serialize(verifiedModeTokenHashes);
            Properties.Settings.Default.Save();
        }

        private static void MarkTokenVerified(string prefix, string token)
        {
            verifiedModeTokenHashes[prefix] = ComputeTokenFingerprint(token);
            SaveVerifiedModeTokens();
        }

        private static void MigrateModeTokenKeys()
        {
            var changed = false;
            if (modeTokens.Remove("Regular", out var regularToken))
            {
                if (!modeTokens.ContainsKey("PVP"))
                {
                    modeTokens["PVP"] = regularToken;
                }
                changed = true;
            }
            if (modeTokens.Remove("Seasonal", out var seasonalToken))
            {
                if (!modeTokens.ContainsKey("SN1"))
                {
                    modeTokens["SN1"] = seasonalToken;
                }
                changed = true;
            }
            if (changed)
            {
                SaveModeTokens();
            }
        }

        private static void RemoveInvalidPrefixAssignments()
        {
            var invalidPrefixes = modeTokens
                .Where(pair => !IsSupportedPrefix(pair.Key)
                    || !string.Equals(GetTokenPrefix(pair.Value), pair.Key, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToList();
            if (invalidPrefixes.Count == 0)
            {
                return;
            }
            foreach (var prefix in invalidPrefixes)
            {
                modeTokens.Remove(prefix);
            }
            SaveModeTokens();
        }

        public static string GetApiBaseUrl(string trackerDomain)
        {
            if (trackerDomain == "tarkovtracker.org")
            {
                return "https://api.tarkovtracker.org";
            }
            return $"https://{trackerDomain}/api/v2";
        }

        public static ITarkovTrackerAPI InitAPI()
        {
            var nextApi = RestService.For<ITarkovTrackerAPI>(GetApiBaseUrl(Properties.Settings.Default.tarkovTrackerDomain));
            nextApi.Client.DefaultRequestHeaders.UserAgent.TryParseAdd($"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name} {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");

            lock (stateLock)
            {
                api = nextApi;
                activationGeneration++;
                currentProfile = "";
                currentSessionMode = EftSessionMode.Unknown;
                activeToken = "";
                ValidToken = false;
                Progress = new();
                return api;
            }
        }

        public static void ResetActiveProfile()
        {
            DeactivateProfile();
        }

        public static string GetToken(string profileId)
        {
            lock (stateLock)
            {
                return tokens.TryGetValue(profileId, out var token) ? token : "";
            }
        }

        public static string NormalizeSessionMode(string sessionMode)
        {
            var trimmedMode = sessionMode?.Trim() ?? "";
            if (string.Equals(trimmedMode, "PVP", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmedMode, "Regular", StringComparison.OrdinalIgnoreCase))
            {
                return "Regular";
            }
            if (string.Equals(trimmedMode, "PVE", StringComparison.OrdinalIgnoreCase))
            {
                return "PVE";
            }
            if (string.Equals(trimmedMode, "Seasonal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmedMode, "PvpSeason", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmedMode, "SN1", StringComparison.OrdinalIgnoreCase))
            {
                return "Seasonal";
            }
            return string.IsNullOrWhiteSpace(trimmedMode) ? "Regular" : trimmedMode;
        }

        public static string NormalizeSessionMode(EftSessionMode sessionMode)
        {
            return sessionMode.ToString();
        }

        public static string GetPrefixForSessionMode(string sessionMode)
        {
            var normalizedMode = NormalizeSessionMode(sessionMode);
            if (string.Equals(normalizedMode, "Regular", StringComparison.OrdinalIgnoreCase))
            {
                return "PVP";
            }
            if (string.Equals(normalizedMode, "PVE", StringComparison.OrdinalIgnoreCase))
            {
                return "PVE";
            }
            // if (string.Equals(normalizedMode, "Seasonal", StringComparison.OrdinalIgnoreCase)) return "SN1";
            // Enable only after EFT's released Seasonal Session mode value is confirmed.
            return "";
        }

        public static string GetPrefixForSessionMode(EftSessionMode sessionMode)
        {
            return GetPrefixForSessionMode(NormalizeSessionMode(sessionMode));
        }

        public static string GetImportedToken(string prefix)
        {
            modeTokens.TryGetValue(prefix.ToUpperInvariant(), out var token);
            if (token == null
                || !string.Equals(GetTokenPrefix(token), prefix, StringComparison.OrdinalIgnoreCase)
                || !IsTokenVerified(prefix, token))
            {
                return "";
            }
            return token;
        }

        public static IReadOnlyDictionary<string, string> GetImportedTokens()
        {
            return modeTokens;
        }

        private static void SaveModeTokens()
        {
            Properties.Settings.Default.tarkovTrackerModeTokens = JsonSerializer.Serialize(modeTokens);
            Properties.Settings.Default.Save();
        }

        public static void SetImportedToken(string prefix, string token)
        {
            var normalizedPrefix = prefix.Trim().ToUpperInvariant();
            var trimmedToken = token.Trim();
            lock (stateLock)
            {
                if (string.IsNullOrWhiteSpace(trimmedToken))
                {
                    modeTokens.Remove(normalizedPrefix);
                    verifiedModeTokenHashes.Remove(normalizedPrefix);
                }
                else
                {
                    modeTokens[normalizedPrefix] = trimmedToken;
                    if (!IsTokenVerified(normalizedPrefix, trimmedToken))
                    {
                        verifiedModeTokenHashes.Remove(normalizedPrefix);
                    }
                }

                if (!IsLegacyService
                    && string.Equals(
                        GetPrefixForSessionMode(currentSessionMode),
                        normalizedPrefix,
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(activeToken, GetImportedToken(normalizedPrefix), StringComparison.Ordinal))
                {
                    activeToken = "";
                    activationGeneration++;
                    ValidToken = false;
                    Progress = new();
                }
            }
            SaveModeTokens();
            SaveVerifiedModeTokens();
        }

        public static string GetTokenPrefix(string apiToken)
        {
            var trimmedToken = apiToken.Trim();
            var separatorIndex = trimmedToken.IndexOf('_');
            if (separatorIndex <= 0)
            {
                return "";
            }
            return trimmedToken[..separatorIndex].ToUpperInvariant();
        }

        public static string GetPrefixDisplayName(string prefix)
        {
            return prefix.ToUpperInvariant() switch
            {
                "PVP" => "Regular (PVP)",
                "PVE" => "PVE",
                // "SN1" => "Seasonal", // Enable with the Seasonal user interface.
                _ => "Unknown",
            };
        }

        public static string GetSessionDisplayName(string sessionMode)
        {
            var normalizedMode = NormalizeSessionMode(sessionMode);
            if (string.Equals(normalizedMode, "PVE", StringComparison.OrdinalIgnoreCase))
            {
                return "PVE";
            }
            if (string.Equals(normalizedMode, "Regular", StringComparison.OrdinalIgnoreCase))
            {
                return "Regular (PVP)";
            }
            if (string.Equals(normalizedMode, "Seasonal", StringComparison.OrdinalIgnoreCase))
            {
                return "Seasonal";
            }
            return "Unknown";
        }

        public static string GetSessionDisplayName(EftSessionMode sessionMode)
        {
            return GetSessionDisplayName(NormalizeSessionMode(sessionMode));
        }

        public static bool IsSupportedPrefix(string prefix)
        {
            return prefix.Equals("PVP", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("PVE", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("SN1", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsImportablePrefix(string prefix)
        {
            return prefix.Equals("PVP", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("PVE", StringComparison.OrdinalIgnoreCase)
                // || prefix.Equals("SN1", StringComparison.OrdinalIgnoreCase) // Enable with the Seasonal user interface.
                ;
        }

        public static async Task<ImportedToken> ImportToken(string apiToken)
        {
            ValidateImportTokenLocally(apiToken);
            var trimmedToken = apiToken.Trim();
            var suppliedPrefix = GetTokenPrefix(trimmedToken);
            if (modeTokens.TryGetValue(suppliedPrefix, out var storedToken)
                && string.Equals(storedToken, trimmedToken, StringComparison.Ordinal)
                && IsTokenVerified(suppliedPrefix, storedToken))
            {
                throw new DuplicateImportedTokenException(
                    $"This {GetPrefixDisplayName(suppliedPrefix)} API key is already verified and saved locally.");
            }
            BeginImportValidationCall();
            try
            {
                var response = await TestToken(trimmedToken);
                if (!response.permissions.Contains("WP"))
                {
                    throw new Exception("This API key is valid but does not have write permission.");
                }
                var prefix = GetVerifiedImportPrefix(trimmedToken, response);
                SetImportedToken(prefix, trimmedToken);
                MarkTokenVerified(prefix, trimmedToken);
                CompleteImportValidationCall(true);
                return new ImportedToken(prefix, GetPrefixDisplayName(prefix));
            }
            catch
            {
                CompleteImportValidationCall(false);
                throw;
            }
        }

        private static void ValidateImportTokenLocally(string apiToken)
        {
            if (string.IsNullOrEmpty(apiToken))
            {
                throw new Exception("Paste a TarkovTracker.org API key before validating.");
            }
            if (!string.Equals(apiToken, apiToken.Trim(), StringComparison.Ordinal))
            {
                throw new Exception("The API key contains a space before or after the key. Copy it directly from TarkovTracker.org and try again.");
            }
            if (apiToken.Any(character => character > 127))
            {
                throw new Exception("The API key contains a non-ASCII character. Copy it directly from TarkovTracker.org instead of typing or editing it manually.");
            }

            var separatorIndex = apiToken.IndexOf('_');
            var hasOneSeparator = separatorIndex == apiToken.LastIndexOf('_');
            var prefix = separatorIndex > 0 ? apiToken[..separatorIndex] : "";
            var identifier = separatorIndex >= 0 && separatorIndex < apiToken.Length - 1
                ? apiToken[(separatorIndex + 1)..]
                : "";
            var identifierIsHexadecimal = identifier.All(Uri.IsHexDigit);
            if (separatorIndex != 3
                || !hasOneSeparator
                || !IsImportablePrefix(prefix)
                || identifier.Length != 18
                || !identifierIsHexadecimal)
            {
                throw new Exception("The API key format is invalid. Expected PVP_ or PVE_ followed by an 18-character hexadecimal identifier. Copy the key directly from TarkovTracker.org and try again.");
            }
        }

        private static string GetVerifiedImportPrefix(string apiToken, TokenResponse response)
        {
            var suppliedPrefix = GetTokenPrefix(apiToken);
            if (!IsImportablePrefix(suppliedPrefix))
            {
                throw new Exception("The verified API key must be a PVP_ or PVE_ key. For accuracy, copy the API key directly from TarkovTracker.org instead of typing or editing it manually.");
            }

            if (!string.IsNullOrWhiteSpace(response.token) && !ApiKeyIdentityMatches(apiToken, response.token))
            {
                throw new Exception("The API key returned by TarkovTracker does not exactly match the imported key. For accuracy, copy the API key directly from TarkovTracker.org instead of typing or editing it manually.");
            }

            // The published API currently exposes only pvp and pve game modes.
            // if (string.Equals(suppliedPrefix, "SN1", StringComparison.OrdinalIgnoreCase))
            // {
            //     return "SN1";
            // }
            // Enable the branch above with IsImportablePrefix when TarkovTracker publishes a Seasonal gameMode value.

            var verifiedPrefix = response.gameMode?.Trim().ToLowerInvariant() switch
            {
                "pvp" => "PVP",
                "pve" => "PVE",
                _ => throw new Exception("TarkovTracker did not return a supported PVP or PVE game mode for this API key."),
            };
            if (!string.Equals(suppliedPrefix, verifiedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception($"This key is marked {GetPrefixDisplayName(suppliedPrefix)}, but TarkovTracker verified it as {GetPrefixDisplayName(verifiedPrefix)}. For accuracy, copy the API key directly from TarkovTracker.org instead of typing or editing it manually.");
            }
            return verifiedPrefix;
        }

        private static bool ApiKeyIdentityMatches(string suppliedToken, string returnedToken)
        {
            var suppliedSeparator = suppliedToken.IndexOf('_');
            var returnedSeparator = returnedToken.IndexOf('_');
            if (suppliedSeparator <= 0 || returnedSeparator <= 0)
            {
                return false;
            }

            var suppliedPrefix = suppliedToken[..suppliedSeparator];
            var returnedPrefix = returnedToken[..returnedSeparator];
            var suppliedIdentifier = suppliedToken[(suppliedSeparator + 1)..];
            var returnedIdentifier = returnedToken[(returnedSeparator + 1)..];
            return string.Equals(suppliedPrefix, returnedPrefix, StringComparison.OrdinalIgnoreCase)
                && string.Equals(suppliedIdentifier, returnedIdentifier, StringComparison.Ordinal);
        }

        private static void BeginImportValidationCall()
        {
            lock (importValidationLock)
            {
                var now = DateTimeOffset.UtcNow;
                if (now < nextImportValidationAllowedAt)
                {
                    var secondsRemaining = Math.Max(1, (int)Math.Ceiling((nextImportValidationAllowedAt - now).TotalSeconds));
                    throw new Exception($"Please wait {secondsRemaining} seconds before verifying another API key.");
                }
                if (importValidationInProgress)
                {
                    throw new Exception("An API key verification is already in progress.");
                }
                importValidationInProgress = true;
            }
        }

        private static void CompleteImportValidationCall(bool succeeded)
        {
            lock (importValidationLock)
            {
                importValidationInProgress = false;
                nextImportValidationAllowedAt = succeeded
                    ? DateTimeOffset.MinValue
                    : DateTimeOffset.UtcNow.Add(importValidationInterval);
            }
        }

        public static int GetImportValidationCooldownSeconds()
        {
            lock (importValidationLock)
            {
                return Math.Max(0, (int)Math.Ceiling((nextImportValidationAllowedAt - DateTimeOffset.UtcNow).TotalSeconds));
            }
        }

        public static bool SetModeToken(string sessionMode, string token)
        {
            var prefix = GetPrefixForSessionMode(sessionMode);
            if (prefix == "" || !string.Equals(GetTokenPrefix(token), prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (modeTokens.TryGetValue(prefix, out var existingToken)
                && !ApiKeyIdentityMatches(token, existingToken))
            {
                return false;
            }
            SetImportedToken(prefix, token);
            return true;
        }

        public static string GetTokenForProfile(Profile profile)
        {
            return IsLegacyService ? GetToken(profile.Id) : GetImportedToken(GetPrefixForSessionMode(profile.SessionMode));
        }

        public record ImportedToken(string Prefix, string DisplayName);

        public sealed class DuplicateImportedTokenException : Exception
        {
            public DuplicateImportedTokenException(string message) : base(message)
            {
            }
        }
        public record PendingTokenValidation(string Token, string DisplayName);

        public static void SetToken(string profileId, string token)
        {
            if (profileId == "")
            {
                throw new Exception("No EFT profile initialized, please launch Escape from Tarkov first");
            }

            string serializedTokens;
            lock (stateLock)
            {
                var previousToken = tokens.TryGetValue(profileId, out var storedToken) ? storedToken : "";
                tokens[profileId] = token;
                serializedTokens = JsonSerializer.Serialize(tokens);

                // Editing the active profile's token immediately revokes the previous
                // activation. No task write may use the old verified state afterward.
                if (profileId == currentProfile && previousToken != token)
                {
                    activationGeneration++;
                    ValidToken = false;
                    Progress = new();
                }
            }

            Properties.Settings.Default.tarkovTrackerTokens = serializedTokens;
            Properties.Settings.Default.Save();
        }

        public static void DeactivateProfile()
        {
            lock (stateLock)
            {
                currentProfile = "";
                currentSessionMode = EftSessionMode.Unknown;
                activeToken = "";
                activationGeneration++;
                ValidToken = false;
                Progress = new();
            }
        }

        public static ProgressResponse? GetActiveProgressSnapshot(Profile expectedProfile)
        {
            lock (stateLock)
            {
                return ValidToken
                    && currentProfile == expectedProfile.Id
                    && currentSessionMode == expectedProfile.SessionMode
                    ? Progress
                    : null;
            }
        }

        public static ProgressResponse? GetActiveProgressSnapshot(string expectedProfileId)
        {
            lock (stateLock)
            {
                return ValidToken && currentProfile == expectedProfileId ? Progress : null;
            }
        }

        public static async Task<ProgressResponse> SetProfile(Profile profile, bool forceRefresh = false)
        {
            if (profile.Id == "")
            {
                throw new Exception("Can't set the EFT profile, please launch Escape from Tarkov and then restart this application");
            }

            var profileSnapshot = profile.Snapshot();
            var newToken = GetTokenForProfile(profile);
            long generation;
            ITarkovTrackerAPI targetApi;
            lock (stateLock)
            {
                if (currentProfile == profileSnapshot.Id
                    && currentSessionMode == profileSnapshot.SessionMode
                    && activeToken == newToken
                    && !forceRefresh
                    && (ValidToken || string.IsNullOrWhiteSpace(newToken)))
                {
                    return Progress;
                }

                currentProfile = profileSnapshot.Id;
                currentSessionMode = profileSnapshot.SessionMode;
                activeToken = newToken;
                generation = ++activationGeneration;
                targetApi = api;

                // Clear the previous profile before the first await. Until both token
                // inspection and progress retrieval finish, writes must remain disabled.
                ValidToken = false;
                Progress = new();
            }

            if (string.IsNullOrWhiteSpace(newToken))
            {
                return Progress;
            }

            await ActivateProfile(new ActiveRequest(
                profileSnapshot.Id,
                profileSnapshot.SessionMode,
                newToken,
                generation,
                targetApi));
            lock (stateLock)
            {
                return Progress;
            }
        }

        public static Task<ProgressResponse> SetProfile(string profileId, bool forceRefresh = false)
        {
            EftSessionMode sessionMode;
            lock (stateLock)
            {
                sessionMode = currentSessionMode;
            }
            return SetProfile(new Profile { Id = profileId, SessionMode = sessionMode }, forceRefresh);
        }

        private static string Bearer(string token) => $"Bearer {token}";

        private static ActiveRequest CaptureActiveRequest(
            string? expectedProfileId = null,
            EftSessionMode? expectedSessionMode = null)
        {
            lock (stateLock)
            {
                if (expectedProfileId != null && expectedProfileId != currentProfile)
                {
                    throw new Exception("Tarkov Tracker profile changed before the task update; the update was not sent");
                }
                if (expectedSessionMode != null && expectedSessionMode != currentSessionMode)
                {
                    throw new Exception("Tarkov Tracker session mode changed before the task update; the update was not sent");
                }
                if (!ValidToken
                    || string.IsNullOrWhiteSpace(currentProfile)
                    || string.IsNullOrWhiteSpace(activeToken))
                {
                    throw new Exception("Invalid token");
                }

                return new ActiveRequest(currentProfile, currentSessionMode, activeToken, activationGeneration, api);
            }
        }

        private static bool IsCurrentLocked(ActiveRequest request)
        {
            var selectedToken = GetTokenForProfile(new Profile
            {
                Id = request.ProfileId,
                SessionMode = request.SessionMode,
            });
            return request.Generation == activationGeneration
                && request.ProfileId == currentProfile
                && request.SessionMode == currentSessionMode
                && request.Token == activeToken
                && request.Token == selectedToken
                && ReferenceEquals(request.Api, api);
        }

        private static bool IsCurrent(ActiveRequest request)
        {
            lock (stateLock)
            {
                return IsCurrentLocked(request);
            }
        }

        private static bool TryInvalidate(ActiveRequest request)
        {
            lock (stateLock)
            {
                if (!IsCurrentLocked(request))
                {
                    return false;
                }

                Progress = new();
                ValidToken = false;
                activationGeneration++;
                return true;
            }
        }

        // stateLock must be held by the caller so an old request cannot update a
        // newly activated profile's in-memory progress.
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

        private static async Task SendTaskStatus(ActiveRequest request, string questId, TaskStatus status)
        {
            try
            {
                await request.Api.SetTaskStatus(questId, TaskStatusBody.From(status), Bearer(request.Token));
                lock (stateLock)
                {
                    if (IsCurrent(request))
                    {
                        SyncStoredStatus(questId, status);
                    }
                }
            }
            catch (ApiException ex)
            {
                if (ex.StatusCode == HttpStatusCode.Unauthorized)
                {
                    InvalidTokenException(request);
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
        }

        public static async Task<string> SetTaskStatus(
            string questId,
            TaskStatus status,
            string? expectedProfileId = null,
            EftSessionMode? expectedSessionMode = null)
        {
            await SendTaskStatus(CaptureActiveRequest(expectedProfileId, expectedSessionMode), questId, status);
            return "success";
        }

        public static async Task<string> SetTaskComplete(
            string questId,
            string? expectedProfileId = null,
            EftSessionMode? expectedSessionMode = null)
        {
            var request = CaptureActiveRequest(expectedProfileId, expectedSessionMode);
            await SendTaskStatus(request, questId, TaskStatus.Finished);
            try
            {
                lock (stateLock)
                {
                    if (!IsCurrent(request))
                    {
                        return "success";
                    }

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
            }
            catch (Exception)
            {
                // Preserve the successful remote update if optional local inference fails.
            }
            return "success";
        }

        public static async Task<string> SetTaskFailed(
            string questId,
            string? expectedProfileId = null,
            EftSessionMode? expectedSessionMode = null)
        {
            return await SetTaskStatus(questId, TaskStatus.Failed, expectedProfileId, expectedSessionMode);
        }

        public static async Task<string> SetTaskStarted(
            string questId,
            string? expectedProfileId = null,
            EftSessionMode? expectedSessionMode = null)
        {
            ActiveRequest request;
            bool shouldWrite;
            lock (stateLock)
            {
                request = CaptureActiveRequest(expectedProfileId, expectedSessionMode);
                shouldWrite = Progress.data.tasksProgress.Any(taskStatus => taskStatus.id == questId && taskStatus.failed);
            }

            if (!shouldWrite)
            {
                return "task not marked as failed";
            }

            await SendTaskStatus(request, questId, TaskStatus.Started);
            return "success";
        }

        public static async Task<string> SetTaskStatuses(
            Dictionary<string, TaskStatus> statuses,
            string? expectedProfileId = null,
            EftSessionMode? expectedSessionMode = null)
        {
            var request = CaptureActiveRequest(expectedProfileId, expectedSessionMode);
            List<TaskStatusBody> body = new();
            foreach (var kvp in statuses)
            {
                TaskStatusBody status = TaskStatusBody.From(kvp.Value);
                status.id = kvp.Key;
                body.Add(status);
            }
            try
            {
                await request.Api.SetTaskStatuses(body, Bearer(request.Token));
                lock (stateLock)
                {
                    if (IsCurrent(request))
                    {
                        foreach (var kvp in statuses)
                        {
                            SyncStoredStatus(kvp.Key, kvp.Value);
                        }
                    }
                }
            }
            catch (ApiException ex)
            {
                if (ex.StatusCode == HttpStatusCode.Unauthorized)
                {
                    InvalidTokenException(request);
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
            var request = CaptureActiveRequest();
            try
            {
                var progress = await request.Api.GetProgress(Bearer(request.Token));
                if (TryPublishProgress(request, progress))
                {
                    ProgressRetrieved?.Invoke(null, new(request.ProfileId, request.SessionMode, progress));
                }
                return progress;
            }
            catch (ApiException ex)
            {
                if (ex.StatusCode == HttpStatusCode.Unauthorized)
                {
                    InvalidTokenException(request);
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

        public static async Task<TokenResponse> TestToken(string apiToken, bool activate = false)
        {
            var trimmedToken = apiToken.Trim();
            if (!activate)
            {
                return await InspectToken(trimmedToken);
            }

            ActiveRequest request;
            lock (stateLock)
            {
                if (string.IsNullOrWhiteSpace(currentProfile))
                {
                    throw new Exception("No EFT profile initialized, please launch Escape from Tarkov first");
                }

                activeToken = trimmedToken;
                ValidToken = false;
                Progress = new();
                request = new ActiveRequest(
                    currentProfile,
                    currentSessionMode,
                    trimmedToken,
                    ++activationGeneration,
                    api);
            }

            return await ActivateProfile(request);
        }

        private static async Task<TokenResponse> InspectToken(string apiToken)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{GetApiBaseUrl(Properties.Settings.Default.tarkovTrackerDomain).TrimEnd('/')}/token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

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
                return response;
            }
        }

        private static async Task<TokenResponse> ActivateProfile(ActiveRequest request)
        {
            try
            {
                var response = await request.Api.TestToken(Bearer(request.Token));
                if (!response.permissions.Contains("WP"))
                {
                    if (TryInvalidate(request))
                    {
                        TokenInvalid?.Invoke(null, new EventArgs());
                    }
                    return response;
                }

                // A token is not active until its matching progress has also loaded.
                // This prevents old progress from being exposed during a mode switch.
                var progress = await request.Api.GetProgress(Bearer(request.Token));
                if (TryPublishProgress(request, progress))
                {
                    TokenValidated?.Invoke(null, new EventArgs());
                    ProgressRetrieved?.Invoke(null, new(request.ProfileId, request.SessionMode, progress));
                }
                return response;
            }
            catch (ApiException ex)
            {
                if (ex.StatusCode == HttpStatusCode.Unauthorized)
                {
                    InvalidTokenException(request);
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
        }

        private static bool TryPublishProgress(ActiveRequest request, ProgressResponse progress)
        {
            lock (stateLock)
            {
                if (!IsCurrentLocked(request))
                {
                    return false;
                }

                Progress = progress;
                ValidToken = true;
                return true;
            }
        }

        private static void InvalidTokenException(ActiveRequest request)
        {
            if (TryInvalidate(request))
            {
                TokenInvalid?.Invoke(null, new EventArgs());
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
