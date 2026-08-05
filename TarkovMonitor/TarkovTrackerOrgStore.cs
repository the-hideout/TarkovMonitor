using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovMonitor
{
    internal sealed class TarkovTrackerOrgStoreDocument
    {
        public const int CurrentVersion = 3;

        [JsonPropertyName("version")]
        [JsonRequired]
        public int Version { get; set; } = CurrentVersion;

        [JsonPropertyName("keys")]
        [JsonRequired]
        public List<TarkovTrackerOrgKey> Keys { get; set; } = new();

        [JsonPropertyName("accounts")]
        public List<TarkovTrackerOrgAccount>? Accounts { get; set; } = new();

        [JsonPropertyName("profiles")]
        public List<TarkovTrackerOrgProfile>? Profiles { get; set; } = new();
    }

    internal sealed class TarkovTrackerOrgProfile
    {
        [JsonPropertyName("accountId")]
        public string AccountId { get; set; } = "";

        [JsonPropertyName("profileId")]
        public string ProfileId { get; set; } = "";

        [JsonPropertyName("sessionMode")]
        public string SessionMode { get; set; } = "";

        [JsonPropertyName("firstSeenUtc")]
        public DateTimeOffset? FirstSeenUtc { get; set; }

        [JsonPropertyName("lastSeenUtc")]
        public DateTimeOffset? LastSeenUtc { get; set; }

        internal TarkovTrackerOrgProfile Clone()
        {
            return new TarkovTrackerOrgProfile
            {
                AccountId = AccountId,
                ProfileId = ProfileId,
                SessionMode = SessionMode,
                FirstSeenUtc = FirstSeenUtc,
                LastSeenUtc = LastSeenUtc,
            };
        }

        internal Profile ToProfile()
        {
            var resolvedMode = GameWatcher.ResolveSessionMode(SessionMode);
            return new Profile
            {
                AccountId = AccountId,
                Id = ProfileId,
                SessionMode = resolvedMode,
                Type = GameWatcher.ResolveProfileType(resolvedMode),
            };
        }
    }

    internal sealed class TarkovTrackerOrgAccount
    {
        [JsonPropertyName("accountId")]
        public string AccountId { get; set; } = "";

        [JsonPropertyName("nickname")]
        public string Nickname { get; set; } = "";

        internal TarkovTrackerOrgAccount Clone()
        {
            return new TarkovTrackerOrgAccount
            {
                AccountId = AccountId,
                Nickname = Nickname,
            };
        }
    }

    internal sealed class TarkovTrackerOrgKey
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("token")]
        public string Token { get; set; } = "";

        [JsonPropertyName("prefix")]
        public string Prefix { get; set; } = "";

        [JsonPropertyName("accountId")]
        public string AccountId { get; set; } = "";

        [JsonPropertyName("profileId")]
        public string ProfileId { get; set; } = "";

        [JsonPropertyName("sessionMode")]
        public string SessionMode { get; set; } = "";

        [JsonPropertyName("verifiedTokenHash")]
        public string VerifiedTokenHash { get; set; } = "";

        // Version 1 stored nicknames on individual keys. This read-only
        // compatibility property is consumed during the version 2 migration.
        [JsonPropertyName("nickname")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LegacyNickname { get; set; }

        [JsonIgnore]
        public bool IsBound => !string.IsNullOrWhiteSpace(AccountId)
            && !string.IsNullOrWhiteSpace(ProfileId)
            && !string.IsNullOrWhiteSpace(SessionMode);

        internal TarkovTrackerOrgKey Clone()
        {
            return new TarkovTrackerOrgKey
            {
                Id = Id,
                Token = Token,
                Prefix = Prefix,
                AccountId = AccountId,
                ProfileId = ProfileId,
                SessionMode = SessionMode,
                VerifiedTokenHash = VerifiedTokenHash,
                LegacyNickname = LegacyNickname,
            };
        }
    }

    internal sealed class TarkovTrackerOrgStore
    {
        private readonly List<TarkovTrackerOrgKey> keys;
        private readonly List<TarkovTrackerOrgAccount> accounts;
        private readonly List<TarkovTrackerOrgProfile> profiles;

        private TarkovTrackerOrgStore(
            IEnumerable<TarkovTrackerOrgKey>? keys = null,
            IEnumerable<TarkovTrackerOrgAccount>? accounts = null,
            IEnumerable<TarkovTrackerOrgProfile>? profiles = null)
        {
            this.keys = keys?.Select(key => key.Clone()).ToList() ?? new();
            this.accounts = accounts?.Select(account => account.Clone()).ToList() ?? new();
            this.profiles = profiles?.Select(profile => profile.Clone()).ToList() ?? new();
        }

        public static TarkovTrackerOrgStore Empty() => new();

        public static bool TryParse(string rawValue, out TarkovTrackerOrgStore store, out string error)
        {
            store = Empty();
            error = "";
            try
            {
                if (string.IsNullOrWhiteSpace(rawValue))
                {
                    throw new JsonException("The stored value is empty.");
                }

                var document = JsonSerializer.Deserialize<TarkovTrackerOrgStoreDocument>(rawValue)
                    ?? throw new JsonException("The stored value is null.");
                if (document.Version is not (1 or 2 or TarkovTrackerOrgStoreDocument.CurrentVersion))
                {
                    throw new JsonException($"Unsupported store version {document.Version}.");
                }
                if (document.Keys == null)
                {
                    throw new JsonException("The key list is null.");
                }

                if (document.Keys.Any(key => key == null))
                {
                    throw new JsonException("The key list contains a null record.");
                }

                var normalizedKeys = document.Keys.Select(Normalize).ToList();
                if (normalizedKeys.Any(key => string.IsNullOrWhiteSpace(key.Id)))
                {
                    throw new JsonException("A stored key record has no identifier.");
                }
                if (normalizedKeys.GroupBy(key => key.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
                {
                    throw new JsonException("The stored key list contains duplicate identifiers.");
                }

                List<TarkovTrackerOrgAccount> normalizedAccounts;
                if (document.Version == 1)
                {
                    normalizedAccounts = normalizedKeys
                        .Where(key => key.IsBound && !string.IsNullOrWhiteSpace(key.LegacyNickname))
                        .GroupBy(key => key.AccountId, StringComparer.Ordinal)
                        .Select(group =>
                        {
                            var nicknames = group
                                .Select(key => NormalizeNickname(key.LegacyNickname))
                                .Distinct(StringComparer.Ordinal)
                                .ToList();
                            if (nicknames.Count != 1)
                            {
                                throw new JsonException($"Account ID {group.Key} has conflicting saved nicknames.");
                            }
                            return new TarkovTrackerOrgAccount
                            {
                                AccountId = group.Key,
                                Nickname = nicknames[0],
                            };
                        })
                        .ToList();
                }
                else
                {
                    if (document.Accounts == null)
                    {
                        throw new JsonException("The account list is null.");
                    }
                    if (document.Accounts.Any(account => account == null))
                    {
                        throw new JsonException("The account list contains a null record.");
                    }
                    normalizedAccounts = document.Accounts.Select(Normalize).ToList();
                }

                if (normalizedAccounts.GroupBy(account => account.AccountId, StringComparer.Ordinal).Any(group => group.Count() > 1))
                {
                    throw new JsonException("The stored account list contains duplicate Account IDs.");
                }
                if (normalizedAccounts.Any(account => !IsValidAccountNickname(account.AccountId, account.Nickname)))
                {
                    throw new JsonException("A stored account nickname record is invalid.");
                }

                List<TarkovTrackerOrgProfile> normalizedProfiles;
                if (document.Version < TarkovTrackerOrgStoreDocument.CurrentVersion)
                {
                    normalizedProfiles = normalizedKeys
                        .Where(key => key.IsBound)
                        .Select(key => new TarkovTrackerOrgProfile
                        {
                            AccountId = key.AccountId,
                            ProfileId = key.ProfileId,
                            SessionMode = key.SessionMode,
                        })
                        .GroupBy(ProfileIdentity, StringComparer.Ordinal)
                        .Select(group => group.First())
                        .ToList();
                }
                else
                {
                    normalizedProfiles = document.Profiles?
                        .Where(profile => profile != null)
                        .Select(Normalize)
                        .ToList() ?? new();
                }

                normalizedProfiles = normalizedProfiles
                    .Where(IsValidProfile)
                    .GroupBy(ProfileIdentity, StringComparer.Ordinal)
                    .Select(MergeProfileObservations)
                    .ToList();

                foreach (var key in normalizedKeys)
                {
                    key.LegacyNickname = null;
                }
                store = new TarkovTrackerOrgStore(normalizedKeys, normalizedAccounts, normalizedProfiles);
                return true;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
            {
                error = ex.Message;
                return false;
            }
        }

        public TarkovTrackerOrgStore Clone() => new(keys, accounts, profiles);

        public string Serialize()
        {
            return JsonSerializer.Serialize(new TarkovTrackerOrgStoreDocument
            {
                Keys = keys.Select(key => key.Clone()).ToList(),
                Accounts = accounts.Select(account => account.Clone()).ToList(),
                Profiles = profiles.Select(profile => profile.Clone()).ToList(),
            });
        }

        public IReadOnlyList<TarkovTrackerOrgKey> GetKeys()
        {
            return keys.Select(key => key.Clone()).ToList();
        }

        public TarkovTrackerOrgKey? GetKey(string id)
        {
            return keys.FirstOrDefault(key => string.Equals(key.Id, id, StringComparison.Ordinal))?.Clone();
        }

        public IReadOnlyList<TarkovTrackerOrgProfile> GetProfiles()
        {
            return profiles.Select(profile => profile.Clone()).ToList();
        }

        public string GetAccountNickname(string accountId)
        {
            return accounts.FirstOrDefault(account => string.Equals(
                account.AccountId,
                accountId,
                StringComparison.Ordinal))?.Nickname ?? "";
        }

        public TarkovTrackerOrgKey? GetForProfile(Profile profile)
        {
            var prefix = TarkovTracker.GetPrefixForSessionMode(profile.SessionMode);
            var sessionMode = TarkovTracker.NormalizeSessionMode(profile.SessionMode);
            return keys.FirstOrDefault(key => key.IsBound
                && string.IsNullOrEmpty(GetStoreIssue(key))
                && string.Equals(key.Prefix, prefix, StringComparison.OrdinalIgnoreCase)
                && string.Equals(key.AccountId, profile.AccountId, StringComparison.Ordinal)
                && string.Equals(key.ProfileId, profile.Id, StringComparison.Ordinal)
                && string.Equals(key.SessionMode, sessionMode, StringComparison.OrdinalIgnoreCase))?.Clone();
        }

        public bool HasPendingKey()
        {
            return keys.Any(key => !key.IsBound);
        }

        public bool HasPendingKey(string prefix)
        {
            return keys.Any(key => !key.IsBound
                && string.Equals(key.Prefix, prefix, StringComparison.OrdinalIgnoreCase));
        }

        public bool ContainsToken(string token)
        {
            return keys.Any(key => TokenIdentityMatches(key.Token, token));
        }

        public TarkovTrackerOrgKey AddVerifiedToken(string token, string prefix, Profile? bindingProfile)
        {
            var normalizedToken = token.Trim();
            var normalizedPrefix = prefix.Trim().ToUpperInvariant();
            if (!TarkovTracker.IsImportableToken(normalizedToken)
                || !string.Equals(
                    normalizedPrefix,
                    TarkovTracker.GetTokenPrefix(normalizedToken),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The verified API key and mode prefix do not match.", nameof(token));
            }
            if (keys.Any(key => TokenIdentityMatches(key.Token, normalizedToken)))
            {
                throw new TarkovTracker.DuplicateImportedTokenException(
                    $"This {TarkovTracker.GetPrefixDisplayName(normalizedPrefix)} API key is already saved locally.");
            }
            if (HasPendingKey(normalizedPrefix))
            {
                throw new InvalidOperationException(
                    $"Bind or remove the pending {TarkovTracker.GetPrefixDisplayName(normalizedPrefix)} API key before importing another one.");
            }

            var key = new TarkovTrackerOrgKey
            {
                Id = Guid.NewGuid().ToString("N"),
                Token = normalizedToken,
                Prefix = normalizedPrefix,
                VerifiedTokenHash = ComputeTokenFingerprint(normalizedToken),
            };

            if (bindingProfile != null && CanBindToProfile(key, bindingProfile))
            {
                ApplyBinding(key, bindingProfile);
                EnsureBindingAvailable(key);
            }

            keys.Add(key);
            return key.Clone();
        }

        public TarkovTrackerOrgKey AddVerifiedBoundToken(string token, string prefix, Profile bindingProfile)
        {
            var key = AddVerifiedToken(token, prefix, bindingProfile);
            if (!key.IsBound)
            {
                keys.RemoveAll(candidate => string.Equals(candidate.Id, key.Id, StringComparison.Ordinal));
                throw new InvalidOperationException(
                    $"The {TarkovTracker.GetPrefixDisplayName(prefix)} API key does not match the selected {bindingProfile.DisplayName} profile.");
            }
            return key;
        }

        public TarkovTrackerOrgKey Bind(string id, Profile profile)
        {
            var key = GetMutableKey(id);
            if (key.IsBound)
            {
                throw new InvalidOperationException("This API key is already bound.");
            }
            if (!IsVerified(key))
            {
                throw new InvalidOperationException("This API key must be verified before it can be bound.");
            }
            if (!string.IsNullOrEmpty(GetStoreIssue(key)))
            {
                throw new InvalidOperationException("This API key record must be repaired before it can be bound.");
            }
            if (!CanBindToProfile(key, profile))
            {
                throw new InvalidOperationException(
                    $"This {TarkovTracker.GetPrefixDisplayName(key.Prefix)} API key cannot be bound to the selected {profile.DisplayName} profile.");
            }

            ApplyBinding(key, profile);
            EnsureBindingAvailable(key);
            return key.Clone();
        }

        public TarkovTrackerOrgKey BindKnownProfile(string id, string accountId, string profileId, EftSessionMode sessionMode)
        {
            var profile = GetKnownProfile(accountId, profileId, sessionMode);
            return Bind(id, profile);
        }

        public TarkovTrackerOrgKey? RememberAndAutoBind(Profile profile, DateTimeOffset seenAt)
        {
            RememberProfile(profile, seenAt, seenAt);
            var existing = GetForProfile(profile);
            if (existing != null || !profile.SupportsTarkovTrackerWrites)
            {
                return existing;
            }

            var prefix = TarkovTracker.GetPrefixForSessionMode(profile.SessionMode);
            var pending = keys
                .Where(key => !key.IsBound
                    && string.IsNullOrEmpty(GetStoreIssue(key))
                    && string.Equals(key.Prefix, prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return pending.Count == 1 ? Bind(pending[0].Id, profile) : null;
        }

        public int RememberProfiles(IEnumerable<TarkovTrackerOrgProfile> discoveredProfiles)
        {
            var changed = 0;
            foreach (var discoveredProfile in discoveredProfiles)
            {
                var normalized = Normalize(discoveredProfile.Clone());
                if (!IsValidProfile(normalized))
                {
                    continue;
                }
                if (RememberProfile(
                    normalized.ToProfile(),
                    normalized.FirstSeenUtc,
                    normalized.LastSeenUtc))
                {
                    changed++;
                }
            }
            return changed;
        }

        public (TarkovTrackerOrgKey Key, TarkovTrackerOrgKey? SwappedKey) ReassignKnownProfile(
            string id,
            string accountId,
            string profileId,
            EftSessionMode sessionMode)
        {
            var key = GetMutableKey(id);
            if (!key.IsBound)
            {
                throw new InvalidOperationException("This API key is not bound.");
            }

            var target = GetKnownProfile(accountId, profileId, sessionMode);
            if (!CanBindToProfile(key, target))
            {
                throw new InvalidOperationException(
                    $"This {TarkovTracker.GetPrefixDisplayName(key.Prefix)} API key cannot be assigned to the selected {target.DisplayName} profile.");
            }
            if (BindingMatches(key, target))
            {
                throw new InvalidOperationException("This API key is already assigned to that profile.");
            }

            var previous = new Profile
            {
                AccountId = key.AccountId,
                Id = key.ProfileId,
                SessionMode = GameWatcher.ResolveSessionMode(key.SessionMode),
                Type = GameWatcher.ResolveProfileType(GameWatcher.ResolveSessionMode(key.SessionMode)),
            };
            var occupant = keys.FirstOrDefault(candidate => candidate.IsBound
                && !string.Equals(candidate.Id, key.Id, StringComparison.Ordinal)
                && BindingMatches(candidate, target));

            ApplyBinding(key, target);
            if (occupant != null)
            {
                if (!CanBindToProfile(occupant, previous))
                {
                    throw new InvalidOperationException("The target profile is occupied by an incompatible API key.");
                }
                ApplyBinding(occupant, previous);
            }

            EnsureBindingAvailable(key);
            if (occupant != null)
            {
                EnsureBindingAvailable(occupant);
            }
            return (key.Clone(), occupant?.Clone());
        }

        public TarkovTrackerOrgKey Unbind(string id)
        {
            var key = GetMutableKey(id);
            if (!key.IsBound)
            {
                throw new InvalidOperationException("This API key is already unbound.");
            }
            if (keys.Any(candidate => !candidate.IsBound
                && !string.Equals(candidate.Id, key.Id, StringComparison.Ordinal)
                && string.Equals(candidate.Prefix, key.Prefix, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Bind or remove the pending {TarkovTracker.GetPrefixDisplayName(key.Prefix)} API key before unbinding another one.");
            }

            key.AccountId = "";
            key.ProfileId = "";
            key.SessionMode = "";
            return key.Clone();
        }

        public string SetAccountNickname(string accountId, string nickname)
        {
            if (!keys.Any(key => key.IsBound
                && string.IsNullOrEmpty(GetStoreIssue(key))
                && string.Equals(key.AccountId, accountId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Bind an API key to this account before setting its nickname.");
            }

            var normalizedAccountId = accountId.Trim();
            var normalizedNickname = NormalizeNickname(nickname);
            if (!IsValidAccountNickname(normalizedAccountId, normalizedNickname))
            {
                throw new ArgumentException("Enter a nickname between 1 and 32 characters.", nameof(nickname));
            }

            var account = accounts.FirstOrDefault(candidate => string.Equals(
                candidate.AccountId,
                normalizedAccountId,
                StringComparison.Ordinal));
            if (account == null)
            {
                accounts.Add(new TarkovTrackerOrgAccount
                {
                    AccountId = normalizedAccountId,
                    Nickname = normalizedNickname,
                });
            }
            else
            {
                account.Nickname = normalizedNickname;
            }
            return normalizedNickname;
        }

        public bool Remove(string id)
        {
            return keys.RemoveAll(key => string.Equals(key.Id, id, StringComparison.Ordinal)) > 0;
        }

        public static bool IsVerified(TarkovTrackerOrgKey key)
        {
            return !string.IsNullOrWhiteSpace(key.Token)
                && IsRecognizedStoredToken(key.Token)
                && string.Equals(key.Prefix, TarkovTracker.GetTokenPrefix(key.Token), StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    key.VerifiedTokenHash,
                    ComputeTokenFingerprint(key.Token),
                    StringComparison.Ordinal);
        }

        public static string ComputeTokenFingerprint(string token)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));
        }

        private static bool IsRecognizedStoredToken(string token)
        {
            if (string.IsNullOrEmpty(token) || token != token.Trim())
            {
                return false;
            }
            var pieces = token.Split('_');
            return pieces.Length == 2
                && pieces[0] is "PVE" or "PVP" or "SN1"
                && pieces[1].Length == 18
                && pieces[1].All(Uri.IsHexDigit);
        }

        private static TarkovTrackerOrgKey Normalize(TarkovTrackerOrgKey key)
        {
            key.Id = key.Id?.Trim() ?? "";
            // Never silently normalize a saved secret. Import performs its own
            // strict validation; persisted whitespace must remain visible to the
            // record validator so the key is quarantined instead of activated.
            key.Token ??= "";
            key.Prefix = key.Prefix?.Trim().ToUpperInvariant() ?? "";
            key.AccountId = key.AccountId?.Trim() ?? "";
            key.ProfileId = key.ProfileId?.Trim() ?? "";
            key.SessionMode = key.SessionMode?.Trim() ?? "";
            key.VerifiedTokenHash = key.VerifiedTokenHash?.Trim().ToUpperInvariant() ?? "";
            key.LegacyNickname = key.LegacyNickname?.Trim();
            return key;
        }

        private static TarkovTrackerOrgAccount Normalize(TarkovTrackerOrgAccount account)
        {
            account.AccountId = account.AccountId?.Trim() ?? "";
            account.Nickname = NormalizeNickname(account.Nickname);
            return account;
        }

        private static TarkovTrackerOrgProfile Normalize(TarkovTrackerOrgProfile profile)
        {
            profile.AccountId = profile.AccountId?.Trim() ?? "";
            profile.ProfileId = profile.ProfileId?.Trim() ?? "";
            profile.SessionMode = TarkovTracker.NormalizeSessionMode(
                GameWatcher.ResolveSessionMode(profile.SessionMode));
            return profile;
        }

        private static string NormalizeNickname(string? nickname) => nickname?.Trim() ?? "";

        private static bool IsValidAccountNickname(string accountId, string nickname)
        {
            return !string.IsNullOrWhiteSpace(accountId)
                && accountId.All(char.IsDigit)
                && nickname.Length is >= 1 and <= 32
                && !nickname.Any(char.IsControl);
        }

        private static bool IsValidProfile(TarkovTrackerOrgProfile profile)
        {
            var resolvedMode = GameWatcher.ResolveSessionMode(profile.SessionMode);
            return !string.IsNullOrWhiteSpace(profile.AccountId)
                && profile.AccountId.All(char.IsDigit)
                && !string.IsNullOrWhiteSpace(profile.ProfileId)
                && resolvedMode is EftSessionMode.PVE or EftSessionMode.Regular or EftSessionMode.Seasonal
                && string.Equals(
                    profile.SessionMode,
                    TarkovTracker.NormalizeSessionMode(resolvedMode),
                    StringComparison.OrdinalIgnoreCase)
                && (profile.FirstSeenUtc == null
                    || profile.LastSeenUtc == null
                    || profile.FirstSeenUtc <= profile.LastSeenUtc);
        }

        private static TarkovTrackerOrgProfile MergeProfileObservations(
            IEnumerable<TarkovTrackerOrgProfile> observations)
        {
            var records = observations.ToList();
            var merged = records[0].Clone();
            merged.FirstSeenUtc = records
                .Where(record => record.FirstSeenUtc != null)
                .Select(record => record.FirstSeenUtc)
                .Min();
            merged.LastSeenUtc = records
                .Where(record => record.LastSeenUtc != null)
                .Select(record => record.LastSeenUtc)
                .Max();
            return merged;
        }

        private bool RememberProfile(
            Profile profile,
            DateTimeOffset? firstSeenUtc,
            DateTimeOffset? lastSeenUtc)
        {
            var candidate = Normalize(new TarkovTrackerOrgProfile
            {
                AccountId = profile.AccountId,
                ProfileId = profile.Id,
                SessionMode = TarkovTracker.NormalizeSessionMode(profile.SessionMode),
                FirstSeenUtc = firstSeenUtc,
                LastSeenUtc = lastSeenUtc,
            });
            if (!IsValidProfile(candidate))
            {
                return false;
            }

            var existing = profiles.FirstOrDefault(saved => string.Equals(
                ProfileIdentity(saved),
                ProfileIdentity(candidate),
                StringComparison.Ordinal));
            if (existing == null)
            {
                profiles.Add(candidate);
                return true;
            }

            var previousFirstSeen = existing.FirstSeenUtc;
            var previousLastSeen = existing.LastSeenUtc;
            if (candidate.FirstSeenUtc != null
                && (existing.FirstSeenUtc == null || candidate.FirstSeenUtc < existing.FirstSeenUtc))
            {
                existing.FirstSeenUtc = candidate.FirstSeenUtc;
            }
            if (candidate.LastSeenUtc != null
                && (existing.LastSeenUtc == null || candidate.LastSeenUtc > existing.LastSeenUtc))
            {
                existing.LastSeenUtc = candidate.LastSeenUtc;
            }
            return previousFirstSeen != existing.FirstSeenUtc || previousLastSeen != existing.LastSeenUtc;
        }

        public Profile GetKnownProfile(string accountId, string profileId, EftSessionMode sessionMode)
        {
            var identity = ProfileIdentity(accountId, profileId, sessionMode);
            return profiles.FirstOrDefault(profile => string.Equals(
                    ProfileIdentity(profile),
                    identity,
                    StringComparison.Ordinal))?.ToProfile()
                ?? throw new InvalidOperationException("The selected EFT profile is not in the saved profile list. Scan profiles and try again.");
        }

        private static string ProfileIdentity(TarkovTrackerOrgProfile profile)
        {
            return ProfileIdentity(
                profile.AccountId,
                profile.ProfileId,
                GameWatcher.ResolveSessionMode(profile.SessionMode));
        }

        private static string ProfileIdentity(string accountId, string profileId, EftSessionMode sessionMode)
        {
            return $"{accountId.Trim()}\u001f{profileId.Trim()}\u001f{TarkovTracker.NormalizeSessionMode(sessionMode).ToUpperInvariant()}";
        }

        private static bool BindingMatches(TarkovTrackerOrgKey key, Profile profile)
        {
            return string.Equals(key.AccountId, profile.AccountId, StringComparison.Ordinal)
                && string.Equals(key.ProfileId, profile.Id, StringComparison.Ordinal)
                && string.Equals(
                    key.SessionMode,
                    TarkovTracker.NormalizeSessionMode(profile.SessionMode),
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool CanBindToProfile(TarkovTrackerOrgKey key, Profile profile)
        {
            return profile.HasIdentity
                && profile.AccountId.All(char.IsDigit)
                && !string.IsNullOrWhiteSpace(profile.Id)
                && profile.SupportsTarkovTrackerWrites
                && string.Equals(
                    key.Prefix,
                    TarkovTracker.GetPrefixForSessionMode(profile.SessionMode),
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyBinding(TarkovTrackerOrgKey key, Profile profile)
        {
            key.AccountId = profile.AccountId;
            key.ProfileId = profile.Id;
            key.SessionMode = TarkovTracker.NormalizeSessionMode(profile.SessionMode);
        }

        private void EnsureBindingAvailable(TarkovTrackerOrgKey proposedKey)
        {
            if (keys.Any(key => key.IsBound
                && !string.Equals(key.Id, proposedKey.Id, StringComparison.Ordinal)
                && string.Equals(key.AccountId, proposedKey.AccountId, StringComparison.Ordinal)
                && string.Equals(key.ProfileId, proposedKey.ProfileId, StringComparison.Ordinal)
                && string.Equals(key.SessionMode, proposedKey.SessionMode, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "The selected EFT account, profile, and mode already have a bound API key. Unbind or remove it before binding another key.");
            }
        }

        private TarkovTrackerOrgKey GetMutableKey(string id)
        {
            return keys.FirstOrDefault(key => string.Equals(key.Id, id, StringComparison.Ordinal))
                ?? throw new KeyNotFoundException("The saved API key was not found.");
        }

        private static bool TokenIdentityMatches(string first, string second)
        {
            return string.Equals(
                ComputeTokenFingerprint(first),
                ComputeTokenFingerprint(second),
                StringComparison.Ordinal);
        }

        public static string GetRecordIssue(TarkovTrackerOrgKey key)
        {
            if (!IsVerified(key))
            {
                return "The token verification record is invalid.";
            }

            var hasAnyBindingField = !string.IsNullOrWhiteSpace(key.AccountId)
                || !string.IsNullOrWhiteSpace(key.ProfileId)
                || !string.IsNullOrWhiteSpace(key.SessionMode);
            if (!key.IsBound && hasAnyBindingField)
            {
                return "The binding is incomplete.";
            }
            if (key.IsBound)
            {
                var resolvedMode = GameWatcher.ResolveSessionMode(key.SessionMode);
                var expectedPrefix = resolvedMode == EftSessionMode.Seasonal
                    ? "SN1"
                    : TarkovTracker.GetPrefixForSessionMode(resolvedMode);
                if (!key.AccountId.All(char.IsDigit)
                    || resolvedMode is not (EftSessionMode.Regular or EftSessionMode.PVE or EftSessionMode.Seasonal)
                    || !string.Equals(
                        key.Prefix,
                        expectedPrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return "The account, profile, or mode binding is invalid.";
                }
            }
            return "";
        }

        public string GetStoreIssue(TarkovTrackerOrgKey key)
        {
            var recordIssue = GetRecordIssue(key);
            if (!string.IsNullOrEmpty(recordIssue))
            {
                return recordIssue;
            }
            if (!key.IsBound && keys.Count(candidate => !candidate.IsBound
                && string.Equals(candidate.Prefix, key.Prefix, StringComparison.OrdinalIgnoreCase)) > 1)
            {
                return "The store contains more than one pending key for the same mode.";
            }
            if (keys.Any(candidate => !string.Equals(candidate.Id, key.Id, StringComparison.Ordinal)
                && TokenIdentityMatches(candidate.Token, key.Token)))
            {
                return "The store contains the same token more than once.";
            }
            if (key.IsBound && keys.Any(candidate => candidate.IsBound
                && !string.Equals(candidate.Id, key.Id, StringComparison.Ordinal)
                && string.Equals(candidate.AccountId, key.AccountId, StringComparison.Ordinal)
                && string.Equals(candidate.ProfileId, key.ProfileId, StringComparison.Ordinal)
                && string.Equals(candidate.SessionMode, key.SessionMode, StringComparison.OrdinalIgnoreCase)))
            {
                return "The store contains more than one key for the same binding.";
            }
            return "";
        }
    }
}
