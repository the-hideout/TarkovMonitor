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
            Task<TokenResponse> TestToken([Header("Authorization")] string authorization, CancellationToken cancellationToken = default);

            [Get("/progress")]
            Task<ProgressResponse> GetProgress([Header("Authorization")] string authorization, CancellationToken cancellationToken = default);

            [Post("/progress/task/{id}")]
            Task<string> SetTaskStatus(string id, [Body] TaskStatusBody body, [Header("Authorization")] string authorization, CancellationToken cancellationToken = default);

            [Post("/progress/tasks")]
            Task<string> SetTaskStatuses([Body] List<TaskStatusBody> body, [Header("Authorization")] string authorization, CancellationToken cancellationToken = default);
        }

        private static readonly HttpClient tokenInspectionClient = new();
        internal readonly record struct ActiveRequest(
            string ProfileId,
            EftSessionMode SessionMode,
            string Token,
            long Generation,
            ITarkovTrackerAPI Api,
            CancellationToken CancellationToken);

        internal sealed class ProfileActivationLease
        {
            internal ActiveRequest Request { get; }
            public ProgressResponse Progress { get; }

            internal ProfileActivationLease(ActiveRequest request, ProgressResponse progress)
            {
                Request = request;
                Progress = progress;
            }
        }

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
        private static CancellationTokenSource activeRequestCancellation = new();
        private static ITarkovTrackerAPI api = InitAPI();

        public static ProgressResponse Progress { get; private set; } = new();
        public static bool ValidToken { get; private set; } = false;
        // TarkovTracker.io compatibility store. Keys are EFT profile IDs.
        private static Dictionary<string, string> tokens = new();
        // TarkovTracker.org store. Keys are source-controlled API key prefixes.
        private static Dictionary<string, string> modeTokens = new(StringComparer.OrdinalIgnoreCase);
        // Fingerprints recorded here prove which exact .org tokens passed the /token endpoint.
        private static Dictionary<string, string> verifiedModeTokenHashes = new(StringComparer.OrdinalIgnoreCase);
        private static bool legacyTokenStoreLoaded;
        private static bool modeTokenStoreLoaded;
        private static bool verificationStoreLoaded;
        private static readonly List<string> storageWarnings = new();
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
            legacyTokenStoreLoaded = TryDeserializeTokenStore(
                Properties.Settings.Default.tarkovTrackerTokens,
                StringComparer.Ordinal,
                nameof(Properties.Settings.Default.tarkovTrackerTokens),
                out tokens);
            modeTokenStoreLoaded = TryDeserializeTokenStore(
                Properties.Settings.Default.tarkovTrackerModeTokens,
                StringComparer.OrdinalIgnoreCase,
                nameof(Properties.Settings.Default.tarkovTrackerModeTokens),
                out modeTokens);
            verificationStoreLoaded = TryDeserializeTokenStore(
                Properties.Settings.Default.tarkovTrackerVerifiedModeTokenHashes,
                StringComparer.OrdinalIgnoreCase,
                nameof(Properties.Settings.Default.tarkovTrackerVerifiedModeTokenHashes),
                out verifiedModeTokenHashes);
            if (modeTokenStoreLoaded && verificationStoreLoaded)
            {
                RemoveStaleVerificationRecords();
            }
        }

        private static bool TryDeserializeTokenStore(
            string rawValue,
            IEqualityComparer<string> comparer,
            string settingName,
            out Dictionary<string, string> store)
        {
            store = new Dictionary<string, string>(comparer);
            try
            {
                if (string.IsNullOrWhiteSpace(rawValue))
                {
                    throw new JsonException("The stored value is empty.");
                }

                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(rawValue)
                    ?? throw new JsonException("The stored value is null.");
                if (parsed.Any(pair => pair.Key is null || pair.Value is null))
                {
                    throw new JsonException("The stored value contains a null key or value.");
                }

                store = new Dictionary<string, string>(parsed, comparer);
                return true;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
            {
                var blockedAction = settingName == nameof(Properties.Settings.Default.tarkovTrackerTokens)
                    ? "Legacy key changes and cleanup are disabled"
                    : "TarkovTracker.org key import and removal are disabled";
                storageWarnings.Add(
                    $"Tarkov Tracker storage setting {settingName} could not be read. Its original value was preserved and Tarkov Monitor will not overwrite it. {blockedAction} until the setting is repaired.");
                return false;
            }
        }

        public static IReadOnlyList<string> GetStorageWarnings()
        {
            lock (stateLock)
            {
                return storageWarnings.ToList();
            }
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
        }

        public static IReadOnlyList<PendingTokenValidation> GetPendingTokenValidations()
        {
            lock (stateLock)
            {
                var candidates = new Dictionary<string, List<PendingTokenSource>>(StringComparer.Ordinal);

                void AddCandidate(string token, PendingTokenSource source)
                {
                    var trimmedToken = token?.Trim() ?? "";
                    if (!IsImportableToken(trimmedToken))
                    {
                        return;
                    }

                    var prefix = GetTokenPrefix(trimmedToken);
                    if (string.Equals(GetImportedToken(prefix), trimmedToken, StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (!candidates.TryGetValue(trimmedToken, out var sources))
                    {
                        sources = new List<PendingTokenSource>();
                        candidates[trimmedToken] = sources;
                    }
                    sources.Add(source);
                }

                if (modeTokenStoreLoaded && verificationStoreLoaded)
                {
                    foreach (var pair in modeTokens.Where(pair => !IsTokenVerified(pair.Key, pair.Value)))
                    {
                        AddCandidate(pair.Value, new(PendingTokenSourceKind.ModePrefix, pair.Key));
                    }
                }

                if (!IsLegacyService)
                {
                    AddCandidate(
                        Properties.Settings.Default.tarkovTrackerToken,
                        new(PendingTokenSourceKind.LegacySingleton, ""));
                    if (legacyTokenStoreLoaded)
                    {
                        foreach (var pair in tokens)
                        {
                            AddCandidate(pair.Value, new(PendingTokenSourceKind.LegacyProfile, pair.Key));
                        }
                    }
                }

                return candidates
                    .Select(pair => new PendingTokenValidation(
                        pair.Key,
                        GetPrefixDisplayName(GetTokenPrefix(pair.Key)),
                        pair.Value.ToList()))
                    .ToList();
            }
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

        public static string GetApiBaseUrl(string trackerDomain)
        {
            if (trackerDomain == "tarkovtracker.org")
            {
                return "https://api.tarkovtracker.org";
            }
            return $"https://{trackerDomain}/api/v2";
        }

        private static long BeginNewActivationLocked()
        {
            activeRequestCancellation.Cancel();
            activeRequestCancellation.Dispose();
            activeRequestCancellation = new CancellationTokenSource();
            return ++activationGeneration;
        }

        public static ITarkovTrackerAPI InitAPI()
        {
            var nextApi = RestService.For<ITarkovTrackerAPI>(GetApiBaseUrl(Properties.Settings.Default.tarkovTrackerDomain));
            nextApi.Client.DefaultRequestHeaders.UserAgent.TryParseAdd($"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name} {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");

            lock (stateLock)
            {
                api = nextApi;
                BeginNewActivationLocked();
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
            return string.IsNullOrWhiteSpace(trimmedMode) ? "Unknown" : trimmedMode;
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
            lock (stateLock)
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
        }

        public static IReadOnlyDictionary<string, string> GetImportedTokens()
        {
            lock (stateLock)
            {
                return new Dictionary<string, string>(modeTokens, StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void EnsureOrgStoresWritable()
        {
            if (!modeTokenStoreLoaded || !verificationStoreLoaded)
            {
                throw new InvalidOperationException("Tarkov Tracker API key storage needs repair before keys can be imported or removed. The unreadable saved value was preserved.");
            }
        }

        private static void EnsureExactLegacyTokenCleanupWritable()
        {
            if (!IsLegacyService && !legacyTokenStoreLoaded)
            {
                throw new InvalidOperationException("Previously saved Tarkov Tracker API key storage needs repair before matching old-key cleanup can run. The unreadable saved value was preserved.");
            }
        }

        public static void SetImportedToken(string prefix, string token)
        {
            StoreImportedToken(prefix, token, markVerified: false);
        }

        public static bool RemoveImportedToken(string prefix)
        {
            if (IsLegacyService)
            {
                return false;
            }
            EnsureOrgStoresWritable();

            var normalizedPrefix = prefix.Trim().ToUpperInvariant();
            if (!IsSupportedPrefix(normalizedPrefix))
            {
                return false;
            }

            lock (stateLock)
            {
                var storedToken = GetImportedToken(normalizedPrefix);
                if (string.IsNullOrWhiteSpace(storedToken))
                {
                    return false;
                }

                EnsureExactLegacyTokenCleanupWritable();
                PersistImportedTokenChangeLocked(
                    normalizedPrefix,
                    "",
                    markVerified: false,
                    matchingTokenCopiesToRemove: storedToken);
                return true;
            }
        }

        private static void StoreImportedToken(string prefix, string token, bool markVerified)
        {
            EnsureOrgStoresWritable();
            var normalizedPrefix = prefix.Trim().ToUpperInvariant();
            var trimmedToken = token.Trim();
            lock (stateLock)
            {
                var matchingTokenCopiesToRemove = markVerified && !IsLegacyService ? trimmedToken : null;
                if (matchingTokenCopiesToRemove != null)
                {
                    EnsureExactLegacyTokenCleanupWritable();
                }
                PersistImportedTokenChangeLocked(
                    normalizedPrefix,
                    trimmedToken,
                    markVerified,
                    matchingTokenCopiesToRemove);
            }
        }

        private static void PersistImportedTokenChangeLocked(
            string normalizedPrefix,
            string token,
            bool markVerified,
            string? matchingTokenCopiesToRemove)
        {
            var originalTokens = new Dictionary<string, string>(tokens, StringComparer.Ordinal);
            var originalModeTokens = new Dictionary<string, string>(modeTokens, StringComparer.OrdinalIgnoreCase);
            var originalVerificationHashes = new Dictionary<string, string>(verifiedModeTokenHashes, StringComparer.OrdinalIgnoreCase);
            var originalSingleton = Properties.Settings.Default.tarkovTrackerToken;
            var originalSerializedTokens = Properties.Settings.Default.tarkovTrackerTokens;
            var originalModeSetting = Properties.Settings.Default.tarkovTrackerModeTokens;
            var originalVerificationSetting = Properties.Settings.Default.tarkovTrackerVerifiedModeTokenHashes;

            if (string.IsNullOrWhiteSpace(token))
            {
                modeTokens.Remove(normalizedPrefix);
                verifiedModeTokenHashes.Remove(normalizedPrefix);
            }
            else
            {
                modeTokens[normalizedPrefix] = token;
                if (markVerified)
                {
                    verifiedModeTokenHashes[normalizedPrefix] = ComputeTokenFingerprint(token);
                }
                else if (!IsTokenVerified(normalizedPrefix, token))
                {
                    verifiedModeTokenHashes.Remove(normalizedPrefix);
                }
            }

            var legacyProfilesChanged = false;
            if (matchingTokenCopiesToRemove != null)
            {
                if (ApiKeyIdentityMatches(
                    Properties.Settings.Default.tarkovTrackerToken,
                    matchingTokenCopiesToRemove))
                {
                    Properties.Settings.Default.tarkovTrackerToken = "";
                }

                var matchingLegacyProfiles = tokens
                    .Where(pair => ApiKeyIdentityMatches(pair.Value, matchingTokenCopiesToRemove))
                    .Select(pair => pair.Key)
                    .ToList();
                foreach (var profileId in matchingLegacyProfiles)
                {
                    tokens.Remove(profileId);
                    legacyProfilesChanged = true;
                }

                var matchingObsoleteModePrefixes = modeTokens
                    .Where(pair => !string.Equals(pair.Key, normalizedPrefix, StringComparison.OrdinalIgnoreCase)
                        && ApiKeyIdentityMatches(pair.Value, matchingTokenCopiesToRemove))
                    .Select(pair => pair.Key)
                    .ToList();
                foreach (var prefix in matchingObsoleteModePrefixes)
                {
                    modeTokens.Remove(prefix);
                    verifiedModeTokenHashes.Remove(prefix);
                }
            }

            try
            {
                if (legacyProfilesChanged)
                {
                    Properties.Settings.Default.tarkovTrackerTokens = JsonSerializer.Serialize(tokens);
                }
                Properties.Settings.Default.tarkovTrackerModeTokens = JsonSerializer.Serialize(modeTokens);
                Properties.Settings.Default.tarkovTrackerVerifiedModeTokenHashes = JsonSerializer.Serialize(verifiedModeTokenHashes);
                Properties.Settings.Default.Save();
            }
            catch
            {
                tokens = originalTokens;
                modeTokens = originalModeTokens;
                verifiedModeTokenHashes = originalVerificationHashes;
                Properties.Settings.Default.tarkovTrackerToken = originalSingleton;
                Properties.Settings.Default.tarkovTrackerTokens = originalSerializedTokens;
                Properties.Settings.Default.tarkovTrackerModeTokens = originalModeSetting;
                Properties.Settings.Default.tarkovTrackerVerifiedModeTokenHashes = originalVerificationSetting;
                throw;
            }

            if (!IsLegacyService
                && string.Equals(
                    GetPrefixForSessionMode(currentSessionMode),
                    normalizedPrefix,
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(activeToken, GetImportedToken(normalizedPrefix), StringComparison.Ordinal))
            {
                activeToken = "";
                BeginNewActivationLocked();
                ValidToken = false;
                Progress = new();
            }
        }

        private static bool IsVerifiedImportedTokenDuplicate(string suppliedPrefix, string suppliedToken)
        {
            lock (stateLock)
            {
                if (!modeTokens.TryGetValue(suppliedPrefix, out var storedToken)
                    || !ApiKeyIdentityMatches(storedToken, suppliedToken)
                    || !IsTokenVerified(suppliedPrefix, storedToken))
                {
                    return false;
                }

                var hasMatchingOldCopies = ApiKeyIdentityMatches(
                        Properties.Settings.Default.tarkovTrackerToken,
                        storedToken)
                    || tokens.Values.Any(token => ApiKeyIdentityMatches(token, storedToken))
                    || modeTokens.Any(pair =>
                        !string.Equals(pair.Key, suppliedPrefix, StringComparison.OrdinalIgnoreCase)
                        && ApiKeyIdentityMatches(pair.Value, storedToken));
                if (hasMatchingOldCopies)
                {
                    PersistImportedTokenChangeLocked(
                        suppliedPrefix,
                        storedToken,
                        markVerified: true,
                        matchingTokenCopiesToRemove: storedToken);
                }

                return true;
            }
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

        public static bool IsImportableToken(string apiToken)
        {
            try
            {
                ValidateImportTokenLocally(apiToken);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static async Task<ImportedToken> ImportToken(string apiToken)
        {
            ValidateImportTokenLocally(apiToken);
            if (IsLegacyService)
            {
                throw new InvalidOperationException("Switch the Tarkov Tracker service to TarkovTracker.org before importing an API key.");
            }
            EnsureOrgStoresWritable();
            EnsureExactLegacyTokenCleanupWritable();
            var trimmedToken = apiToken.Trim();
            var suppliedPrefix = GetTokenPrefix(trimmedToken);
            if (IsVerifiedImportedTokenDuplicate(suppliedPrefix, trimmedToken))
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
                StoreImportedToken(prefix, trimmedToken, markVerified: true);
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
        public enum PendingTokenSourceKind
        {
            ModePrefix,
            LegacySingleton,
            LegacyProfile,
        }

        public record PendingTokenSource(PendingTokenSourceKind Kind, string Key);
        public record PendingTokenValidation(
            string Token,
            string DisplayName,
            IReadOnlyList<PendingTokenSource> Sources);

        public static bool CompletePendingTokenMigration(
            PendingTokenValidation pendingToken,
            ImportedToken importedToken)
        {
            if (IsLegacyService)
            {
                return false;
            }
            var prefix = GetTokenPrefix(pendingToken.Token);
            if (!string.Equals(prefix, importedToken.Prefix, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(GetImportedToken(prefix), pendingToken.Token, StringComparison.Ordinal))
            {
                return false;
            }

            lock (stateLock)
            {
                var legacyProfileSources = pendingToken.Sources
                    .Where(source => source.Kind == PendingTokenSourceKind.LegacyProfile)
                    .ToList();
                if (legacyProfileSources.Count > 0 && !legacyTokenStoreLoaded)
                {
                    return false;
                }

                var originalTokens = new Dictionary<string, string>(tokens, StringComparer.Ordinal);
                var originalModeTokens = new Dictionary<string, string>(modeTokens, StringComparer.OrdinalIgnoreCase);
                var originalVerificationHashes = new Dictionary<string, string>(verifiedModeTokenHashes, StringComparer.OrdinalIgnoreCase);
                var originalSingleton = Properties.Settings.Default.tarkovTrackerToken;
                var originalSerializedTokens = Properties.Settings.Default.tarkovTrackerTokens;
                var originalModeSetting = Properties.Settings.Default.tarkovTrackerModeTokens;
                var originalVerificationSetting = Properties.Settings.Default.tarkovTrackerVerifiedModeTokenHashes;
                var changed = false;
                var legacyProfilesChanged = false;
                var modeStoreChanged = false;

                foreach (var source in pendingToken.Sources)
                {
                    if (source.Kind == PendingTokenSourceKind.LegacySingleton
                        && string.Equals(
                            Properties.Settings.Default.tarkovTrackerToken,
                            pendingToken.Token,
                            StringComparison.Ordinal))
                    {
                        Properties.Settings.Default.tarkovTrackerToken = "";
                        changed = true;
                    }
                    else if (source.Kind == PendingTokenSourceKind.LegacyProfile
                        && tokens.TryGetValue(source.Key, out var storedToken)
                        && string.Equals(storedToken, pendingToken.Token, StringComparison.Ordinal))
                    {
                        tokens.Remove(source.Key);
                        changed = true;
                        legacyProfilesChanged = true;
                    }
                    else if (source.Kind == PendingTokenSourceKind.ModePrefix
                        && !string.Equals(source.Key, importedToken.Prefix, StringComparison.OrdinalIgnoreCase)
                        && modeTokens.TryGetValue(source.Key, out var modeToken)
                        && string.Equals(modeToken, pendingToken.Token, StringComparison.Ordinal))
                    {
                        modeTokens.Remove(source.Key);
                        verifiedModeTokenHashes.Remove(source.Key);
                        changed = true;
                        modeStoreChanged = true;
                    }
                }

                if (!changed)
                {
                    return true;
                }

                try
                {
                    if (legacyProfilesChanged)
                    {
                        Properties.Settings.Default.tarkovTrackerTokens = JsonSerializer.Serialize(tokens);
                    }
                    if (modeStoreChanged)
                    {
                        Properties.Settings.Default.tarkovTrackerModeTokens = JsonSerializer.Serialize(modeTokens);
                        Properties.Settings.Default.tarkovTrackerVerifiedModeTokenHashes = JsonSerializer.Serialize(verifiedModeTokenHashes);
                    }
                    Properties.Settings.Default.Save();
                    return true;
                }
                catch
                {
                    tokens = originalTokens;
                    modeTokens = originalModeTokens;
                    verifiedModeTokenHashes = originalVerificationHashes;
                    Properties.Settings.Default.tarkovTrackerToken = originalSingleton;
                    Properties.Settings.Default.tarkovTrackerTokens = originalSerializedTokens;
                    Properties.Settings.Default.tarkovTrackerModeTokens = originalModeSetting;
                    Properties.Settings.Default.tarkovTrackerVerifiedModeTokenHashes = originalVerificationSetting;
                    return false;
                }
            }
        }

        public static void SetToken(string profileId, string token)
        {
            if (profileId == "")
            {
                throw new Exception("No EFT profile initialized, please launch Escape from Tarkov first");
            }
            if (!legacyTokenStoreLoaded)
            {
                throw new InvalidOperationException("Legacy Tarkov Tracker token storage could not be read, so it was not overwritten.");
            }

            lock (stateLock)
            {
                var originalTokens = new Dictionary<string, string>(tokens, StringComparer.Ordinal);
                var originalSetting = Properties.Settings.Default.tarkovTrackerTokens;
                var previousToken = tokens.TryGetValue(profileId, out var storedToken) ? storedToken : "";
                tokens[profileId] = token;
                try
                {
                    Properties.Settings.Default.tarkovTrackerTokens = JsonSerializer.Serialize(tokens);
                    Properties.Settings.Default.Save();
                }
                catch
                {
                    tokens = originalTokens;
                    Properties.Settings.Default.tarkovTrackerTokens = originalSetting;
                    throw;
                }

                // Editing the active legacy profile's token immediately revokes the
                // previous activation. The separate .org store is unaffected.
                if (IsLegacyService && profileId == currentProfile && previousToken != token)
                {
                    BeginNewActivationLocked();
                    ValidToken = false;
                    Progress = new();
                }
            }
        }

        public static void DeactivateProfile()
        {
            lock (stateLock)
            {
                currentProfile = "";
                currentSessionMode = EftSessionMode.Unknown;
                activeToken = "";
                BeginNewActivationLocked();
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
            CancellationToken requestCancellation;
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
                generation = BeginNewActivationLocked();
                targetApi = api;
                requestCancellation = activeRequestCancellation.Token;

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
                targetApi,
                requestCancellation));
            lock (stateLock)
            {
                return Progress;
            }
        }

        public static async Task<ProfileActivationLease> AcquireProfileLease(Profile profile)
        {
            await SetProfile(profile, forceRefresh: true);
            var request = CaptureActiveRequest(profile.Id, profile.SessionMode);
            lock (stateLock)
            {
                if (!IsCurrentLocked(request))
                {
                    throw new Exception("Tarkov Tracker profile changed before historical processing could begin");
                }
                return new ProfileActivationLease(request, Progress);
            }
        }

        public static void ReleaseProfileLease(ProfileActivationLease lease)
        {
            lock (stateLock)
            {
                if (!IsCurrentLocked(lease.Request))
                {
                    return;
                }

                currentProfile = "";
                currentSessionMode = EftSessionMode.Unknown;
                activeToken = "";
                BeginNewActivationLocked();
                ValidToken = false;
                Progress = new();
            }
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

                return new ActiveRequest(
                    currentProfile,
                    currentSessionMode,
                    activeToken,
                    activationGeneration,
                    api,
                    activeRequestCancellation.Token);
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
                && !request.CancellationToken.IsCancellationRequested
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
                BeginNewActivationLocked();
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
            if (!IsCurrent(request))
            {
                throw new Exception("Tarkov Tracker profile changed before the task update could be sent");
            }
            try
            {
                await request.Api.SetTaskStatus(
                    questId,
                    TaskStatusBody.From(status),
                    Bearer(request.Token),
                    request.CancellationToken);
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
                            if (failCondition.task == questId && failCondition.status?.Contains("complete") == true)
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
            return await SetTaskStatuses(statuses, request);
        }

        public static async Task<string> SetTaskStatuses(
            Dictionary<string, TaskStatus> statuses,
            ProfileActivationLease lease)
        {
            if (!IsCurrent(lease.Request))
            {
                throw new Exception("Tarkov Tracker profile changed before historical task updates could be sent");
            }
            return await SetTaskStatuses(statuses, lease.Request);
        }

        private static async Task<string> SetTaskStatuses(
            Dictionary<string, TaskStatus> statuses,
            ActiveRequest request)
        {
            List<TaskStatusBody> body = new();
            foreach (var kvp in statuses)
            {
                TaskStatusBody status = TaskStatusBody.From(kvp.Value);
                status.id = kvp.Key;
                body.Add(status);
            }
            if (!IsCurrent(request))
            {
                throw new Exception("Tarkov Tracker profile changed before task updates could be sent");
            }
            try
            {
                await request.Api.SetTaskStatuses(body, Bearer(request.Token), request.CancellationToken);
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
                var progress = await request.Api.GetProgress(Bearer(request.Token), request.CancellationToken);
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
                    BeginNewActivationLocked(),
                    api,
                    activeRequestCancellation.Token);
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
                var response = await request.Api.TestToken(Bearer(request.Token), request.CancellationToken);
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
                var progress = await request.Api.GetProgress(Bearer(request.Token), request.CancellationToken);
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
