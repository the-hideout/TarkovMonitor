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
            string AccountId,
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
            public string AccountId { get; }
            public EftSessionMode SessionMode { get; }
            public ProgressResponse Progress { get; }

            public ProgressRetrievedEventArgs(string profileId, string accountId, EftSessionMode sessionMode, ProgressResponse progress)
            {
                ProfileId = profileId;
                AccountId = accountId;
                SessionMode = sessionMode;
                Progress = progress;
            }
        }

        private static readonly object stateLock = new();
        private static long activationGeneration;
        private static long serviceGeneration;
        private static CancellationTokenSource activeRequestCancellation = new();
        private static string activeDomain = Properties.Settings.Default.tarkovTrackerDomain;
        private static ITarkovTrackerAPI api = InitAPI();

        public static ProgressResponse Progress { get; private set; } = new();
        public static bool ValidToken { get; private set; } = false;
        // TarkovTracker.io compatibility store. Keys are EFT profile IDs.
        private static Dictionary<string, string> tokens = new();
        // Pre-split TarkovTracker.org store. It is read only for one-at-a-time
        // recovery into the versioned profile-bound store below.
        private static Dictionary<string, string> modeTokens = new(StringComparer.OrdinalIgnoreCase);
        // Fingerprints for the pre-split .org recovery store.
        private static Dictionary<string, string> verifiedModeTokenHashes = new(StringComparer.OrdinalIgnoreCase);
        private static TarkovTrackerOrgStore orgTokenStore = TarkovTrackerOrgStore.Empty();
        private static bool legacyTokenStoreLoaded;
        private static bool modeTokenStoreLoaded;
        private static bool verificationStoreLoaded;
        private static bool orgTokenStoreLoaded;
        private static readonly List<string> storageWarnings = new();
        private static string currentProfile = "";
        private static string currentAccountId = "";
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

        public sealed class OrgKeyAutoAssignedEventArgs : EventArgs
        {
            public string DisplayName { get; }
            public string AccountId { get; }
            public string ProfileId { get; }
            public EftSessionMode SessionMode { get; }

            public OrgKeyAutoAssignedEventArgs(
                string displayName,
                string accountId,
                string profileId,
                EftSessionMode sessionMode)
            {
                DisplayName = displayName;
                AccountId = accountId;
                ProfileId = profileId;
                SessionMode = sessionMode;
            }
        }
        public static string CurrentAccountId
        {
            get
            {
                lock (stateLock)
                {
                    return currentAccountId;
                }
            }
        }
        public static bool IsLegacyService
        {
            get
            {
                lock (stateLock)
                {
                    return IsLegacyServiceLocked();
                }
            }
        }

        public static event EventHandler<EventArgs>? TokenValidated;
        public static event EventHandler<EventArgs>? TokenInvalid;
        public static event EventHandler<ProgressRetrievedEventArgs>? ProgressRetrieved;
        public static event EventHandler<OrgKeyAutoAssignedEventArgs>? OrgKeyAutoAssigned;
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
            orgTokenStoreLoaded = TarkovTrackerOrgStore.TryParse(
                Properties.Settings.Default.tarkovTrackerOrgTokenStore,
                out orgTokenStore,
                out var orgStoreError);
            if (!orgTokenStoreLoaded)
            {
                storageWarnings.Add(
                    $"TarkovTracker.org key storage could not be read. Its original value was preserved and key changes are disabled until it is repaired. {orgStoreError}");
            }
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
                    ? "Legacy TarkovTracker.io key changes and profile-keyed TarkovTracker.org recovery are disabled"
                    : "Recovery of previously saved TarkovTracker.org keys is unavailable";
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
            var nextDomain = Properties.Settings.Default.tarkovTrackerDomain;
            var nextApi = RestService.For<ITarkovTrackerAPI>(GetApiBaseUrl(nextDomain));
            nextApi.Client.DefaultRequestHeaders.UserAgent.TryParseAdd($"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name} {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");

            lock (stateLock)
            {
                api = nextApi;
                activeDomain = nextDomain;
                serviceGeneration++;
                BeginNewActivationLocked();
                currentProfile = "";
                currentAccountId = "";
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
                return tokens.TryGetValue(profileId, out var token) && !IsImportableToken(token)
                    ? token
                    : "";
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

        private const string LegacyOrgKeyIdPrefix = "legacy-org:";

        private enum LegacyOrgSourceKind
        {
            Mode,
            Profile,
            Singleton,
        }

        private sealed record LegacyOrgSource(LegacyOrgSourceKind Kind, string Key);

        private sealed record LegacyOrgCandidate(
            string Id,
            string Prefix,
            string Token,
            bool Verified,
            IReadOnlyList<LegacyOrgSource> Sources);

        public sealed record OrgKeySummary(
            string Id,
            string Prefix,
            string DisplayName,
            string MaskedToken,
            bool IsBound,
            string AccountId,
            string ProfileId,
            EftSessionMode SessionMode,
            string AccountNickname,
            bool IsVerified,
            bool HasPendingConflict,
            bool IsLegacyRecovery,
            bool IsQuarantined);

        public sealed record OrgProfileSummary(
            string AccountId,
            string ProfileId,
            EftSessionMode SessionMode,
            string DisplayName,
            string AccountNickname,
            DateTimeOffset? FirstSeenUtc,
            DateTimeOffset? LastSeenUtc,
            bool HasBoundKey,
            bool IsCurrent);

        public sealed record OrgReassignmentSummary(
            OrgKeySummary Key,
            OrgKeySummary? SwappedKey);

        public static IReadOnlyList<OrgKeySummary> GetOrgKeys()
        {
            lock (stateLock)
            {
                var storedKeys = orgTokenStore.GetKeys();
                var legacyCandidates = orgTokenStoreLoaded
                    ? GetLegacyOrgCandidatesLocked()
                    : Array.Empty<LegacyOrgCandidate>();
                var conflictedPendingPrefixes = storedKeys
                    .Where(key => !key.IsBound)
                    .Select(key => key.Prefix)
                    .Concat(legacyCandidates.Select(candidate => candidate.Prefix))
                    .GroupBy(prefix => prefix, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var summaries = storedKeys
                    .Select(key =>
                    {
                        var summary = ToSummary(key);
                        return !key.IsBound && conflictedPendingPrefixes.Contains(key.Prefix)
                            ? summary with { HasPendingConflict = true }
                            : summary;
                    })
                    .ToList();

                foreach (var legacyCandidate in legacyCandidates)
                {
                    var summary = ToLegacySummary(legacyCandidate);
                    summaries.Add(conflictedPendingPrefixes.Contains(legacyCandidate.Prefix)
                        ? summary with { HasPendingConflict = true }
                        : summary);
                }
                return summaries;
            }
        }

        public static IReadOnlyList<OrgProfileSummary> GetKnownOrgProfiles()
        {
            lock (stateLock)
            {
                var boundKeys = orgTokenStore.GetKeys().Where(key => key.IsBound).ToList();
                return orgTokenStore.GetProfiles()
                    .Select(profile =>
                    {
                        var sessionMode = GameWatcher.ResolveSessionMode(profile.SessionMode);
                        return new OrgProfileSummary(
                            profile.AccountId,
                            profile.ProfileId,
                            sessionMode,
                            profile.ToProfile().DisplayName,
                            orgTokenStore.GetAccountNickname(profile.AccountId),
                            profile.FirstSeenUtc,
                            profile.LastSeenUtc,
                            boundKeys.Any(key => string.Equals(key.AccountId, profile.AccountId, StringComparison.Ordinal)
                                && string.Equals(key.ProfileId, profile.ProfileId, StringComparison.Ordinal)
                                && string.Equals(key.SessionMode, profile.SessionMode, StringComparison.OrdinalIgnoreCase)),
                            string.Equals(profile.AccountId, currentAccountId, StringComparison.Ordinal)
                                && string.Equals(profile.ProfileId, currentProfile, StringComparison.Ordinal)
                                && sessionMode == currentSessionMode);
                    })
                    .OrderByDescending(profile => profile.IsCurrent)
                    .ThenByDescending(profile => profile.LastSeenUtc)
                    .ToList();
            }
        }

        public static int SaveDiscoveredOrgProfiles(IEnumerable<DiscoveredEftProfile> discoveredProfiles)
        {
            var expectedServiceGeneration = CaptureOrgServiceGeneration();
            EnsureOrgProfileStoreWritable();
            var records = discoveredProfiles.Select(discovered => new TarkovTrackerOrgProfile
            {
                AccountId = discovered.Profile.AccountId,
                ProfileId = discovered.Profile.Id,
                SessionMode = NormalizeSessionMode(discovered.Profile.SessionMode),
                FirstSeenUtc = discovered.FirstSeenUtc,
                LastSeenUtc = discovered.LastSeenUtc,
            }).ToList();
            lock (stateLock)
            {
                EnsureOrgServiceGenerationLocked(expectedServiceGeneration);
                return PersistOrgStoreChangeLocked(store => store.RememberProfiles(records));
            }
        }

        private static bool IsLegacyServiceLocked()
        {
            return string.Equals(activeDomain, "tarkovtracker.io", StringComparison.OrdinalIgnoreCase);
        }

        private static long CaptureOrgServiceGeneration()
        {
            lock (stateLock)
            {
                EnsureOrgServiceGenerationLocked(serviceGeneration);
                return serviceGeneration;
            }
        }

        private static void EnsureOrgServiceGenerationLocked(long expectedGeneration)
        {
            if (IsLegacyServiceLocked() || serviceGeneration != expectedGeneration)
            {
                throw new InvalidOperationException("The Tarkov Tracker service changed. Switch to TarkovTracker.org and try again.");
            }
        }

        public static bool HasPendingOrgKey()
        {
            lock (stateLock)
            {
                return orgTokenStore.HasPendingKey()
                    || (orgTokenStoreLoaded && GetNextLegacyOrgCandidateLocked() != null);
            }
        }

        public static async Task<ImportedToken> ImportOrgToken(string apiToken)
        {
            ValidateImportTokenLocally(apiToken);
            var expectedServiceGeneration = CaptureOrgServiceGeneration();
            var trimmedToken = apiToken.Trim();
            var suppliedPrefix = GetTokenPrefix(trimmedToken);

            EnsureOrgProfileStoreWritable();
            lock (stateLock)
            {
                if (orgTokenStore.HasPendingKey(suppliedPrefix)
                    || GetLegacyOrgCandidateLocked(suppliedPrefix) != null)
                {
                    throw new InvalidOperationException(
                        $"Bind or remove the pending {GetPrefixDisplayName(suppliedPrefix)} API key before importing another one.");
                }
            }

            lock (stateLock)
            {
                if (orgTokenStore.ContainsToken(trimmedToken))
                {
                    throw new DuplicateImportedTokenException(
                        $"This {GetPrefixDisplayName(suppliedPrefix)} API key is already saved locally.");
                }
            }
            BeginImportValidationCall();
            try
            {
                var response = await InspectToken(trimmedToken, "tarkovtracker.org");
                if (!response.permissions.Contains("WP"))
                {
                    throw new Exception("This API key is valid but does not have write permission.");
                }
                var prefix = TarkovTrackerOrgTokenVerifier.GetVerifiedPrefix(
                    trimmedToken,
                    response.token,
                    response.gameMode);
                TarkovTrackerOrgKey savedKey;
                lock (stateLock)
                {
                    EnsureOrgServiceGenerationLocked(expectedServiceGeneration);
                    if (orgTokenStore.HasPendingKey(prefix)
                        || GetLegacyOrgCandidateLocked(prefix) != null)
                    {
                        throw new InvalidOperationException(
                            $"Another pending {GetPrefixDisplayName(prefix)} API key was added before this import finished.");
                    }
                    savedKey = PersistOrgStoreChangeLocked(store =>
                        store.AddVerifiedToken(trimmedToken, prefix, null));
                }
                CompleteImportValidationCall(true);
                return new ImportedToken(
                    savedKey.Id,
                    prefix,
                    GetPrefixDisplayName(prefix));
            }
            catch
            {
                CompleteImportValidationCall(false);
                throw;
            }
        }

        public static Task<OrgKeySummary> BindOrgKey(string id, Profile profile)
        {
            return BindOrgKeyCore(id, profile, requireCurrentProfile: true);
        }

        public static Task<OrgKeySummary> AssignOrgKey(
            string id,
            string accountId,
            string profileId,
            EftSessionMode sessionMode)
        {
            return BindOrgKeyCore(id, new Profile
            {
                AccountId = accountId,
                Id = profileId,
                SessionMode = sessionMode,
                Type = GameWatcher.ResolveProfileType(sessionMode),
            }, requireCurrentProfile: false);
        }

        private static async Task<OrgKeySummary> BindOrgKeyCore(
            string id,
            Profile profile,
            bool requireCurrentProfile)
        {
            var expectedServiceGeneration = CaptureOrgServiceGeneration();
            EnsureOrgProfileStoreWritable();

            if (id.StartsWith(LegacyOrgKeyIdPrefix, StringComparison.Ordinal))
            {
                string prefix;
                string token;
                bool alreadyVerified;
                lock (stateLock)
                {
                    var candidate = GetLegacyOrgCandidateByIdLocked(id);
                    if (candidate == null)
                    {
                        throw new KeyNotFoundException("The pending API key was not found.");
                    }
                    prefix = candidate.Prefix;
                    token = candidate.Token;
                    alreadyVerified = candidate.Verified;
                }

                if (!alreadyVerified)
                {
                    BeginImportValidationCall();
                    try
                    {
                        var response = await InspectToken(token, "tarkovtracker.org");
                        if (!response.permissions.Contains("WP"))
                        {
                            throw new Exception("This API key is valid but does not have write permission.");
                        }
                        var verifiedPrefix = TarkovTrackerOrgTokenVerifier.GetVerifiedPrefix(
                            token,
                            response.token,
                            response.gameMode);
                        if (!string.Equals(prefix, verifiedPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new Exception("The saved API key mode no longer matches its verified mode.");
                        }
                        CompleteImportValidationCall(true);
                    }
                    catch
                    {
                        CompleteImportValidationCall(false);
                        throw;
                    }
                }

                lock (stateLock)
                {
                    EnsureOrgServiceGenerationLocked(expectedServiceGeneration);
                    var bindingProfile = requireCurrentProfile
                        ? EnsureCurrentBindingProfileLocked(profile)
                        : orgTokenStore.GetKnownProfile(profile.AccountId, profile.Id, profile.SessionMode);
                    var candidate = GetLegacyOrgCandidateByIdLocked(id);
                    if (candidate == null
                        || !string.Equals(candidate.Prefix, prefix, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(candidate.Token, token, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("The pending API key changed before binding completed.");
                    }

                    var savedKey = PersistLegacyRecoveryBindingLocked(candidate, bindingProfile);
                    return ToSummary(savedKey);
                }
            }

            lock (stateLock)
            {
                EnsureOrgServiceGenerationLocked(expectedServiceGeneration);
                TarkovTrackerOrgKey boundKey;
                if (requireCurrentProfile)
                {
                    var bindingProfile = EnsureCurrentBindingProfileLocked(profile);
                    boundKey = PersistOrgStoreChangeLocked(store =>
                    {
                        store.RememberAndAutoBind(bindingProfile, DateTimeOffset.UtcNow);
                        var existing = store.GetKey(id);
                        return existing?.IsBound == true
                            ? existing
                            : store.Bind(id, bindingProfile);
                    });
                }
                else
                {
                    boundKey = PersistOrgStoreChangeLocked(store => store.BindKnownProfile(
                        id,
                        profile.AccountId,
                        profile.Id,
                        profile.SessionMode));
                }
                return ToSummary(boundKey);
            }
        }

        public static OrgReassignmentSummary ReassignOrgKey(
            string id,
            string accountId,
            string profileId,
            EftSessionMode sessionMode)
        {
            var expectedServiceGeneration = CaptureOrgServiceGeneration();
            EnsureOrgProfileStoreWritable();
            lock (stateLock)
            {
                EnsureOrgServiceGenerationLocked(expectedServiceGeneration);
                var result = PersistOrgStoreChangeLocked(store => store.ReassignKnownProfile(
                    id,
                    accountId,
                    profileId,
                    sessionMode));
                return new OrgReassignmentSummary(
                    ToSummary(result.Key),
                    result.SwappedKey == null ? null : ToSummary(result.SwappedKey));
            }
        }

        public static bool RemoveOrgKey(string id)
        {
            var expectedServiceGeneration = CaptureOrgServiceGeneration();
            EnsureOrgProfileStoreWritable();

            lock (stateLock)
            {
                EnsureOrgServiceGenerationLocked(expectedServiceGeneration);
                if (id.StartsWith(LegacyOrgKeyIdPrefix, StringComparison.Ordinal))
                {
                    var candidate = GetLegacyOrgCandidateByIdLocked(id);
                    return candidate != null && RemoveLegacyOrgCandidateLocked(candidate);
                }

                return PersistOrgStoreChangeLocked(store => store.Remove(id));
            }
        }

        public static OrgKeySummary UnbindOrgKey(string id)
        {
            var expectedServiceGeneration = CaptureOrgServiceGeneration();
            EnsureOrgProfileStoreWritable();

            lock (stateLock)
            {
                EnsureOrgServiceGenerationLocked(expectedServiceGeneration);
                var key = orgTokenStore.GetKey(id)
                    ?? throw new KeyNotFoundException("The saved API key was not found.");
                if (GetLegacyOrgCandidateLocked(key.Prefix) != null)
                {
                    throw new InvalidOperationException(
                        $"Bind or remove the pending {GetPrefixDisplayName(key.Prefix)} API key before unbinding another one.");
                }
                var unboundKey = PersistOrgStoreChangeLocked(store => store.Unbind(id));
                return ToSummary(unboundKey);
            }
        }

        public static string SetOrgAccountNickname(string accountId, string nickname)
        {
            var expectedServiceGeneration = CaptureOrgServiceGeneration();
            EnsureOrgProfileStoreWritable();

            lock (stateLock)
            {
                EnsureOrgServiceGenerationLocked(expectedServiceGeneration);
                return PersistOrgStoreChangeLocked(store => store.SetAccountNickname(accountId, nickname));
            }
        }

        private static OrgKeySummary ToSummary(TarkovTrackerOrgKey key)
        {
            var sessionMode = key.IsBound
                ? GameWatcher.ResolveSessionMode(key.SessionMode)
                : EftSessionMode.Unknown;
            return new OrgKeySummary(
                key.Id,
                key.Prefix,
                GetPrefixDisplayName(key.Prefix),
                MaskToken(key.Token),
                key.IsBound,
                key.AccountId,
                key.ProfileId,
                sessionMode,
                key.IsBound ? orgTokenStore.GetAccountNickname(key.AccountId) : "",
                TarkovTrackerOrgStore.IsVerified(key),
                false,
                false,
                !string.IsNullOrEmpty(orgTokenStore.GetStoreIssue(key)));
        }

        private static OrgKeySummary ToLegacySummary(LegacyOrgCandidate candidate)
        {
            return new OrgKeySummary(
                candidate.Id,
                candidate.Prefix,
                GetPrefixDisplayName(candidate.Prefix),
                MaskToken(candidate.Token),
                false,
                "",
                "",
                EftSessionMode.Unknown,
                "",
                candidate.Verified,
                false,
                true,
                false);
        }

        private static string MaskToken(string token)
        {
            var prefix = GetTokenPrefix(token);
            return token.Length <= 4 ? $"{prefix}_••••" : $"{prefix}_••••{token[^4..]}";
        }

        private static IReadOnlyList<LegacyOrgCandidate> GetLegacyOrgCandidatesLocked()
        {
            var sources = new List<(string Prefix, string Token, LegacyOrgSource Source)>();
            if (modeTokenStoreLoaded)
            {
                foreach (var pair in modeTokens)
                {
                    var token = pair.Value?.Trim() ?? "";
                    if (!IsImportableToken(token))
                    {
                        continue;
                    }
                    sources.Add((
                        GetTokenPrefix(token),
                        token,
                        new LegacyOrgSource(LegacyOrgSourceKind.Mode, pair.Key)));
                }
            }
            if (legacyTokenStoreLoaded)
            {
                foreach (var pair in tokens)
                {
                    var token = pair.Value?.Trim() ?? "";
                    if (!IsImportableToken(token))
                    {
                        continue;
                    }
                    sources.Add((
                        GetTokenPrefix(token),
                        token,
                        new LegacyOrgSource(LegacyOrgSourceKind.Profile, pair.Key)));
                }
            }

            var singletonToken = Properties.Settings.Default.tarkovTrackerToken?.Trim() ?? "";
            if (IsImportableToken(singletonToken))
            {
                sources.Add((
                    GetTokenPrefix(singletonToken),
                    singletonToken,
                    new LegacyOrgSource(LegacyOrgSourceKind.Singleton, "")));
            }

            var storedFingerprints = orgTokenStore.GetKeys()
                .Select(key => TarkovTrackerOrgStore.ComputeTokenFingerprint(key.Token))
                .ToHashSet(StringComparer.Ordinal);
            return sources
                .GroupBy(
                    source => TarkovTrackerOrgStore.ComputeTokenFingerprint(source.Token),
                    StringComparer.Ordinal)
                .Where(group => !storedFingerprints.Contains(group.Key))
                .Select(group =>
                {
                    var source = group.First();
                    return new LegacyOrgCandidate(
                        $"{LegacyOrgKeyIdPrefix}{group.Key}",
                        source.Prefix,
                        source.Token,
                        verificationStoreLoaded && IsTokenVerified(source.Prefix, source.Token),
                        group.Select(item => item.Source).Distinct().ToList());
                })
                .OrderBy(candidate => candidate.Prefix, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
                .ToList();
        }

        private static LegacyOrgCandidate? GetNextLegacyOrgCandidateLocked()
        {
            return GetLegacyOrgCandidatesLocked().FirstOrDefault();
        }

        private static LegacyOrgCandidate? GetLegacyOrgCandidateLocked(string prefix)
        {
            return GetLegacyOrgCandidatesLocked().FirstOrDefault(candidate => string.Equals(
                candidate.Prefix,
                prefix,
                StringComparison.OrdinalIgnoreCase));
        }

        private static LegacyOrgCandidate? GetLegacyOrgCandidateByIdLocked(string id)
        {
            return GetLegacyOrgCandidatesLocked().FirstOrDefault(candidate => string.Equals(
                candidate.Id,
                id,
                StringComparison.Ordinal));
        }

        private static T PersistOrgStoreChangeLocked<T>(Func<TarkovTrackerOrgStore, T> mutation)
        {
            EnsureOrgProfileStoreWritable();
            var currentSerialized = orgTokenStore.Serialize();
            var candidateStore = orgTokenStore.Clone();
            var result = mutation(candidateStore);
            var candidateSerialized = candidateStore.Serialize();
            if (string.Equals(currentSerialized, candidateSerialized, StringComparison.Ordinal))
            {
                return result;
            }
            var previousSetting = Properties.Settings.Default.tarkovTrackerOrgTokenStore;
            try
            {
                Properties.Settings.Default.tarkovTrackerOrgTokenStore = candidateSerialized;
                Properties.Settings.Default.Save();
                orgTokenStore = candidateStore;
                InvalidateChangedOrgActivationLocked();
                return result;
            }
            catch
            {
                Properties.Settings.Default.tarkovTrackerOrgTokenStore = previousSetting;
                throw;
            }
        }

        private static TarkovTrackerOrgKey PersistLegacyRecoveryBindingLocked(
            LegacyOrgCandidate candidate,
            Profile profile)
        {
            var candidateStore = orgTokenStore.Clone();
            var savedKey = candidateStore.AddVerifiedBoundToken(candidate.Token, candidate.Prefix, profile);
            var previousOrgSetting = Properties.Settings.Default.tarkovTrackerOrgTokenStore;
            var previousModeSetting = Properties.Settings.Default.tarkovTrackerModeTokens;
            var previousLegacySetting = Properties.Settings.Default.tarkovTrackerTokens;
            var previousSingletonSetting = Properties.Settings.Default.tarkovTrackerToken;
            var previousVerificationSetting = Properties.Settings.Default.tarkovTrackerVerifiedModeTokenHashes;
            var previousModeTokens = new Dictionary<string, string>(modeTokens, StringComparer.OrdinalIgnoreCase);
            var previousTokens = new Dictionary<string, string>(tokens, StringComparer.Ordinal);
            var previousVerificationHashes = new Dictionary<string, string>(verifiedModeTokenHashes, StringComparer.OrdinalIgnoreCase);
            try
            {
                RemoveLegacyOrgSourcesInMemoryLocked(candidate);
                Properties.Settings.Default.tarkovTrackerOrgTokenStore = candidateStore.Serialize();
                Properties.Settings.Default.tarkovTrackerModeTokens = JsonSerializer.Serialize(modeTokens);
                Properties.Settings.Default.tarkovTrackerTokens = JsonSerializer.Serialize(tokens);
                if (verificationStoreLoaded)
                {
                    Properties.Settings.Default.tarkovTrackerVerifiedModeTokenHashes = JsonSerializer.Serialize(verifiedModeTokenHashes);
                }
                Properties.Settings.Default.Save();
                orgTokenStore = candidateStore;
                InvalidateChangedOrgActivationLocked();
                return savedKey;
            }
            catch
            {
                modeTokens = previousModeTokens;
                tokens = previousTokens;
                verifiedModeTokenHashes = previousVerificationHashes;
                Properties.Settings.Default.tarkovTrackerOrgTokenStore = previousOrgSetting;
                Properties.Settings.Default.tarkovTrackerModeTokens = previousModeSetting;
                Properties.Settings.Default.tarkovTrackerTokens = previousLegacySetting;
                Properties.Settings.Default.tarkovTrackerToken = previousSingletonSetting;
                Properties.Settings.Default.tarkovTrackerVerifiedModeTokenHashes = previousVerificationSetting;
                throw;
            }
        }

        private static bool RemoveLegacyOrgCandidateLocked(LegacyOrgCandidate candidate)
        {
            var previousModeSetting = Properties.Settings.Default.tarkovTrackerModeTokens;
            var previousLegacySetting = Properties.Settings.Default.tarkovTrackerTokens;
            var previousSingletonSetting = Properties.Settings.Default.tarkovTrackerToken;
            var previousVerificationSetting = Properties.Settings.Default.tarkovTrackerVerifiedModeTokenHashes;
            var previousModeTokens = new Dictionary<string, string>(modeTokens, StringComparer.OrdinalIgnoreCase);
            var previousTokens = new Dictionary<string, string>(tokens, StringComparer.Ordinal);
            var previousVerificationHashes = new Dictionary<string, string>(verifiedModeTokenHashes, StringComparer.OrdinalIgnoreCase);
            try
            {
                RemoveLegacyOrgSourcesInMemoryLocked(candidate);
                Properties.Settings.Default.tarkovTrackerModeTokens = JsonSerializer.Serialize(modeTokens);
                Properties.Settings.Default.tarkovTrackerTokens = JsonSerializer.Serialize(tokens);
                if (verificationStoreLoaded)
                {
                    Properties.Settings.Default.tarkovTrackerVerifiedModeTokenHashes = JsonSerializer.Serialize(verifiedModeTokenHashes);
                }
                Properties.Settings.Default.Save();
                return true;
            }
            catch
            {
                modeTokens = previousModeTokens;
                tokens = previousTokens;
                verifiedModeTokenHashes = previousVerificationHashes;
                Properties.Settings.Default.tarkovTrackerModeTokens = previousModeSetting;
                Properties.Settings.Default.tarkovTrackerTokens = previousLegacySetting;
                Properties.Settings.Default.tarkovTrackerToken = previousSingletonSetting;
                Properties.Settings.Default.tarkovTrackerVerifiedModeTokenHashes = previousVerificationSetting;
                throw;
            }
        }

        private static void RemoveLegacyOrgSourcesInMemoryLocked(LegacyOrgCandidate candidate)
        {
            foreach (var source in candidate.Sources)
            {
                switch (source.Kind)
                {
                    case LegacyOrgSourceKind.Mode:
                        if (!modeTokens.TryGetValue(source.Key, out var modeToken)
                            || !string.Equals(modeToken?.Trim(), candidate.Token, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("The saved API key changed before its recovery source could be updated.");
                        }
                        modeTokens.Remove(source.Key);
                        if (verificationStoreLoaded)
                        {
                            verifiedModeTokenHashes.Remove(candidate.Prefix);
                        }
                        break;
                    case LegacyOrgSourceKind.Profile:
                        if (!tokens.TryGetValue(source.Key, out var profileToken)
                            || !string.Equals(profileToken?.Trim(), candidate.Token, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("The saved API key changed before its recovery source could be updated.");
                        }
                        tokens.Remove(source.Key);
                        break;
                    case LegacyOrgSourceKind.Singleton:
                        if (!string.Equals(
                            Properties.Settings.Default.tarkovTrackerToken?.Trim(),
                            candidate.Token,
                            StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("The saved API key changed before its recovery source could be updated.");
                        }
                        Properties.Settings.Default.tarkovTrackerToken = "";
                        break;
                }
            }
        }

        private static void EnsureOrgProfileStoreWritable()
        {
            if (!orgTokenStoreLoaded)
            {
                throw new InvalidOperationException("TarkovTracker.org key storage needs repair before keys can be changed. The unreadable saved value was preserved.");
            }
        }

        private static Profile EnsureCurrentBindingProfileLocked(Profile profile)
        {
            if (!profile.SupportsTarkovTrackerWrites
                || !string.Equals(profile.Id, currentProfile, StringComparison.Ordinal)
                || !string.Equals(profile.AccountId, currentAccountId, StringComparison.Ordinal)
                || profile.SessionMode != currentSessionMode)
            {
                throw new InvalidOperationException("The selected EFT profile changed. Select the matching profile and try again.");
            }
            return profile.Snapshot();
        }

        private static void InvalidateChangedOrgActivationLocked()
        {
            if (IsLegacyServiceLocked() || string.IsNullOrWhiteSpace(currentProfile))
            {
                return;
            }

            var selectedToken = orgTokenStore.GetForProfile(new Profile
            {
                Id = currentProfile,
                AccountId = currentAccountId,
                SessionMode = currentSessionMode,
                Type = GameWatcher.ResolveProfileType(currentSessionMode),
            })?.Token ?? "";
            if (string.Equals(activeToken, selectedToken, StringComparison.Ordinal))
            {
                return;
            }

            activeToken = "";
            BeginNewActivationLocked();
            ValidToken = false;
            Progress = new();
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
                // Display is safe before import/write support: the separate token,
                // profile, and request gates still keep Seasonal inactive.
                "SN1" => "Seasonal",
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

        internal static bool IsImportablePrefix(string prefix)
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

        public static string GetTokenForProfile(Profile profile)
        {
            lock (stateLock)
            {
                return GetTokenForProfileLocked(profile);
            }
        }

        private static string GetTokenForProfileLocked(Profile profile)
        {
            if (IsLegacyServiceLocked())
            {
                return tokens.TryGetValue(profile.Id, out var token) && !IsImportableToken(token)
                    ? token
                    : "";
            }
            return orgTokenStore.GetForProfile(profile)?.Token ?? "";
        }

        public record ImportedToken(
            string Id,
            string Prefix,
            string DisplayName);

        public sealed class DuplicateImportedTokenException : Exception
        {
            public DuplicateImportedTokenException(string message) : base(message)
            {
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
            if (IsImportableToken(token))
            {
                throw new InvalidOperationException("This is a TarkovTracker.org API key. Switch to TarkovTracker.org to recover or manage it.");
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
                if (IsLegacyServiceLocked() && profileId == currentProfile && previousToken != token)
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
                currentAccountId = "";
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
                    && currentAccountId == expectedProfile.AccountId
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
            string newToken;
            long generation = 0;
            ITarkovTrackerAPI targetApi = null!;
            CancellationToken requestCancellation = default;
            ProgressResponse? unchangedProgress = null;
            OrgKeyAutoAssignedEventArgs? autoAssignment = null;
            lock (stateLock)
            {
                if (!IsLegacyServiceLocked()
                    && orgTokenStoreLoaded
                    && profileSnapshot.HasIdentity
                    && !string.IsNullOrWhiteSpace(profileSnapshot.Id)
                    && profileSnapshot.SessionMode is EftSessionMode.PVE or EftSessionMode.Regular or EftSessionMode.Seasonal)
                {
                    var existingKey = orgTokenStore.GetForProfile(profileSnapshot);
                    var prefix = GetPrefixForSessionMode(profileSnapshot.SessionMode);
                    var allowAutoBind = GetLegacyOrgCandidateLocked(prefix) == null;
                    var selectedKey = PersistOrgStoreChangeLocked(store =>
                        store.RememberAndAutoBind(profileSnapshot, DateTimeOffset.UtcNow, allowAutoBind));
                    newToken = selectedKey?.Token ?? "";
                    if (existingKey == null && selectedKey?.IsBound == true)
                    {
                        autoAssignment = new OrgKeyAutoAssignedEventArgs(
                            GetPrefixDisplayName(selectedKey.Prefix),
                            selectedKey.AccountId,
                            selectedKey.ProfileId,
                            GameWatcher.ResolveSessionMode(selectedKey.SessionMode));
                    }
                }
                else
                {
                    newToken = GetTokenForProfileLocked(profileSnapshot);
                }
                if (currentProfile == profileSnapshot.Id
                    && currentAccountId == profileSnapshot.AccountId
                    && currentSessionMode == profileSnapshot.SessionMode
                    && activeToken == newToken
                    && !forceRefresh
                    && (ValidToken || string.IsNullOrWhiteSpace(newToken)))
                {
                    unchangedProgress = Progress;
                }
                else
                {
                    currentProfile = profileSnapshot.Id;
                    currentAccountId = profileSnapshot.AccountId;
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
            }

            if (autoAssignment != null)
            {
                foreach (EventHandler<OrgKeyAutoAssignedEventArgs> handler in
                    OrgKeyAutoAssigned?.GetInvocationList().Cast<EventHandler<OrgKeyAutoAssignedEventArgs>>()
                    ?? Array.Empty<EventHandler<OrgKeyAutoAssignedEventArgs>>())
                {
                    try
                    {
                        handler(null, autoAssignment);
                    }
                    catch
                    {
                        // Assignment is already persisted. A presentation observer must
                        // never prevent the matching key from continuing activation.
                    }
                }
            }
            if (unchangedProgress != null)
            {
                return unchangedProgress;
            }

            if (string.IsNullOrWhiteSpace(newToken))
            {
                return Progress;
            }

            await ActivateProfile(new ActiveRequest(
                profileSnapshot.Id,
                profileSnapshot.AccountId,
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
            var profileSnapshot = profile.Snapshot();
            await SetProfile(profileSnapshot, forceRefresh: true);
            var request = CaptureActiveRequest(
                profileSnapshot.Id,
                profileSnapshot.SessionMode,
                profileSnapshot.AccountId);
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
                currentAccountId = "";
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
            EftSessionMode? expectedSessionMode = null,
            string? expectedAccountId = null)
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
                if (expectedAccountId != null && expectedAccountId != currentAccountId)
                {
                    throw new Exception("Tarkov Tracker account changed before the task update; the update was not sent");
                }
                if (!ValidToken
                    || string.IsNullOrWhiteSpace(currentProfile)
                    || string.IsNullOrWhiteSpace(activeToken))
                {
                    throw new Exception("Invalid token");
                }

                return new ActiveRequest(
                    currentProfile,
                    currentAccountId,
                    currentSessionMode,
                    activeToken,
                    activationGeneration,
                    api,
                    activeRequestCancellation.Token);
            }
        }

        private static bool IsCurrentLocked(ActiveRequest request)
        {
            var selectedToken = GetTokenForProfileLocked(new Profile
            {
                Id = request.ProfileId,
                AccountId = request.AccountId,
                SessionMode = request.SessionMode,
                Type = GameWatcher.ResolveProfileType(request.SessionMode),
            });
            return request.Generation == activationGeneration
                && !request.CancellationToken.IsCancellationRequested
                && request.ProfileId == currentProfile
                && request.AccountId == currentAccountId
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
            EftSessionMode? expectedSessionMode = null,
            string? expectedAccountId = null)
        {
            await SendTaskStatus(
                CaptureActiveRequest(expectedProfileId, expectedSessionMode, expectedAccountId),
                questId,
                status);
            return "success";
        }

        public static async Task<string> SetTaskComplete(
            string questId,
            string? expectedProfileId = null,
            EftSessionMode? expectedSessionMode = null,
            string? expectedAccountId = null)
        {
            var request = CaptureActiveRequest(expectedProfileId, expectedSessionMode, expectedAccountId);
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
            EftSessionMode? expectedSessionMode = null,
            string? expectedAccountId = null)
        {
            return await SetTaskStatus(
                questId,
                TaskStatus.Failed,
                expectedProfileId,
                expectedSessionMode,
                expectedAccountId);
        }

        public static async Task<string> SetTaskStarted(
            string questId,
            string? expectedProfileId = null,
            EftSessionMode? expectedSessionMode = null,
            string? expectedAccountId = null)
        {
            ActiveRequest request;
            bool shouldWrite;
            lock (stateLock)
            {
                request = CaptureActiveRequest(expectedProfileId, expectedSessionMode, expectedAccountId);
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
            EftSessionMode? expectedSessionMode = null,
            string? expectedAccountId = null)
        {
            var request = CaptureActiveRequest(expectedProfileId, expectedSessionMode, expectedAccountId);
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
                    ProgressRetrieved?.Invoke(null, new(request.ProfileId, request.AccountId, request.SessionMode, progress));
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
                string domain;
                lock (stateLock)
                {
                    domain = activeDomain;
                }
                return await InspectToken(trimmedToken, domain);
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
                    currentAccountId,
                    currentSessionMode,
                    trimmedToken,
                    BeginNewActivationLocked(),
                    api,
                    activeRequestCancellation.Token);
            }

            return await ActivateProfile(request);
        }

        private static async Task<TokenResponse> InspectToken(string apiToken, string trackerDomain)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{GetApiBaseUrl(trackerDomain).TrimEnd('/')}/token");
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
                EnsureCurrentRequest(request);
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
                EnsureCurrentRequest(request);
                var progress = await request.Api.GetProgress(Bearer(request.Token), request.CancellationToken);
                if (TryPublishProgress(request, progress))
                {
                    TokenValidated?.Invoke(null, new EventArgs());
                    ProgressRetrieved?.Invoke(null, new(request.ProfileId, request.AccountId, request.SessionMode, progress));
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

        private static void EnsureCurrentRequest(ActiveRequest request)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(request))
            {
                throw new OperationCanceledException(
                    "Tarkov Tracker profile, key, or service changed before the request could be sent.",
                    request.CancellationToken);
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
