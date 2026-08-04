using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovMonitor
{
    internal sealed class TarkovTrackerOrgStoreDocument
    {
        public const int CurrentVersion = 1;

        [JsonPropertyName("version")]
        [JsonRequired]
        public int Version { get; set; } = CurrentVersion;

        [JsonPropertyName("keys")]
        [JsonRequired]
        public List<TarkovTrackerOrgKey> Keys { get; set; } = new();
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

        [JsonPropertyName("nickname")]
        public string Nickname { get; set; } = "";

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
                Nickname = Nickname,
            };
        }
    }

    internal sealed class TarkovTrackerOrgStore
    {
        private readonly List<TarkovTrackerOrgKey> keys;

        private TarkovTrackerOrgStore(IEnumerable<TarkovTrackerOrgKey>? keys = null)
        {
            this.keys = keys?.Select(key => key.Clone()).ToList() ?? new();
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
                if (document.Version != TarkovTrackerOrgStoreDocument.CurrentVersion)
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

                store = new TarkovTrackerOrgStore(normalizedKeys);
                return true;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
            {
                error = ex.Message;
                return false;
            }
        }

        public TarkovTrackerOrgStore Clone() => new(keys);

        public string Serialize()
        {
            return JsonSerializer.Serialize(new TarkovTrackerOrgStoreDocument
            {
                Keys = keys.Select(key => key.Clone()).ToList(),
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
            if (HasPendingKey())
            {
                throw new InvalidOperationException("Bind or remove the pending API key before importing another key.");
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

        public TarkovTrackerOrgKey Unbind(string id)
        {
            var key = GetMutableKey(id);
            if (!key.IsBound)
            {
                throw new InvalidOperationException("This API key is already unbound.");
            }
            if (keys.Any(candidate => !candidate.IsBound
                && !string.Equals(candidate.Id, key.Id, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Bind or remove the pending API key before unbinding another key.");
            }

            key.AccountId = "";
            key.ProfileId = "";
            key.SessionMode = "";
            return key.Clone();
        }

        public TarkovTrackerOrgKey AssignNickname(string id, string nickname)
        {
            var key = GetMutableKey(id);
            if (!key.IsBound)
            {
                throw new InvalidOperationException("Bind this key first to assign a nickname.");
            }
            if (!string.IsNullOrWhiteSpace(key.Nickname))
            {
                throw new InvalidOperationException("This API key already has a nickname.");
            }

            var normalizedNickname = nickname.Trim();
            if (normalizedNickname.Length is < 1 or > 32
                || normalizedNickname.Any(char.IsControl))
            {
                throw new ArgumentException("Enter a nickname between 1 and 32 characters.", nameof(nickname));
            }

            key.Nickname = normalizedNickname;
            return key.Clone();
        }

        public bool Remove(string id)
        {
            return keys.RemoveAll(key => string.Equals(key.Id, id, StringComparison.Ordinal)) > 0;
        }

        public static bool IsVerified(TarkovTrackerOrgKey key)
        {
            return !string.IsNullOrWhiteSpace(key.Token)
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
            key.Nickname = key.Nickname?.Trim() ?? "";
            return key;
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
            if (!IsVerified(key) || !TarkovTracker.IsImportableToken(key.Token))
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
                if (!key.AccountId.All(char.IsDigit)
                    || resolvedMode is not (EftSessionMode.Regular or EftSessionMode.PVE)
                    || !string.Equals(
                        key.Prefix,
                        TarkovTracker.GetPrefixForSessionMode(resolvedMode),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return "The account, profile, or mode binding is invalid.";
                }
            }
            if (key.Nickname.Length > 32 || key.Nickname.Any(char.IsControl))
            {
                return "The nickname is invalid.";
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
            if (keys.Count(candidate => !candidate.IsBound) > 1 && !key.IsBound)
            {
                return "The store contains more than one pending key.";
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
