using System.Net;
using System.Text.RegularExpressions;

namespace TarkovMonitor
{
    internal readonly record struct TrackerProfileSwitch(
        long Generation,
        string ProfileId,
        ProfileType Mode,
        string Token,
        long EndpointGeneration);

    internal readonly record struct TrackerWriteAuthorization(
        long Generation,
        string ProfileId,
        ProfileType Mode,
        string Token,
        long EndpointGeneration)
    {
        internal string AuthorizationHeader => $"Bearer {Token}";
    }

    internal static class TrackerTokenFormat
    {
        internal static bool IsSupportedOrgToken(string? token)
        {
            var value = token?.Trim() ?? string.Empty;
            if (value.Length != 22 || value[3] != '_')
            {
                return false;
            }

            var prefix = value[..3].ToUpperInvariant();
            return prefix is ("PVE" or "PVP" or "SZN")
                && value[4..].All(Uri.IsHexDigit);
        }

        internal static bool MatchesMode(string? token, ProfileType mode)
        {
            if (!IsSupportedOrgToken(token))
            {
                return false;
            }

            var expectedPrefix = mode switch
            {
                ProfileType.PVE => "PVE",
                ProfileType.Regular => "PVP",
                ProfileType.PvpSeason => "SZN",
                _ => null,
            };
            return expectedPrefix != null
                && token!.StartsWith(expectedPrefix + "_", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class TrackerAuthorizationState<TProgress> where TProgress : class
    {
        private readonly object gate = new();
        private readonly Func<TProgress> emptyProgress;
        private long generation;
        private TrackerWriteAuthorization? current;
        private TrackerProfileSwitch? pending;
        private TProgress progress;

        internal TrackerAuthorizationState(Func<TProgress> emptyProgress)
        {
            this.emptyProgress = emptyProgress;
            progress = emptyProgress();
        }

        internal bool Valid
        {
            get
            {
                lock (gate)
                {
                    return current.HasValue;
                }
            }
        }

        internal string CurrentProfileId
        {
            get
            {
                lock (gate)
                {
                    return current?.ProfileId ?? string.Empty;
                }
            }
        }

        internal TProgress Progress
        {
            get
            {
                lock (gate)
                {
                    return progress;
                }
            }
        }

        internal TrackerProfileSwitch BeginSwitch(Profile profile, string token, long endpointGeneration)
        {
            lock (gate)
            {
                generation++;
                current = null;
                progress = emptyProgress();
                pending = new(
                    generation,
                    profile.Id,
                    profile.Type,
                    token.Trim(),
                    endpointGeneration);
                return pending.Value;
            }
        }

        internal bool TryActivate(
            TrackerProfileSwitch profileSwitch,
            TProgress loadedProgress,
            out TrackerWriteAuthorization authorization)
        {
            lock (gate)
            {
                if (pending != profileSwitch
                    || profileSwitch.Generation != generation
                    || string.IsNullOrWhiteSpace(profileSwitch.ProfileId)
                    || !TrackerTokenFormat.MatchesMode(profileSwitch.Token, profileSwitch.Mode))
                {
                    authorization = default;
                    return false;
                }

                authorization = new(
                    profileSwitch.Generation,
                    profileSwitch.ProfileId,
                    profileSwitch.Mode,
                    profileSwitch.Token,
                    profileSwitch.EndpointGeneration);
                current = authorization;
                pending = null;
                progress = loadedProgress;
                return true;
            }
        }

        internal bool TryAuthorize(Profile profile, out TrackerWriteAuthorization authorization)
        {
            lock (gate)
            {
                if (current is not { } candidate
                    || string.IsNullOrWhiteSpace(profile.Id)
                    || candidate.ProfileId != profile.Id
                    || candidate.Mode != profile.Type
                    || !TrackerTokenFormat.MatchesMode(candidate.Token, candidate.Mode))
                {
                    authorization = default;
                    return false;
                }

                authorization = candidate;
                return true;
            }
        }

        internal bool IsCurrent(TrackerWriteAuthorization authorization)
        {
            lock (gate)
            {
                return current == authorization;
            }
        }

        internal bool IsCurrent(TrackerProfileSwitch profileSwitch)
        {
            lock (gate)
            {
                return pending == profileSwitch
                    && profileSwitch.Generation == generation
                    && current == null;
            }
        }

        internal bool InvalidateProfile(string profileId)
        {
            lock (gate)
            {
                if (!string.Equals(pending?.ProfileId, profileId, StringComparison.Ordinal)
                    && !string.Equals(current?.ProfileId, profileId, StringComparison.Ordinal))
                {
                    return false;
                }
                generation++;
                pending = null;
                current = null;
                progress = emptyProgress();
                return true;
            }
        }

        internal bool UpdateIfCurrent(TrackerWriteAuthorization authorization, Action<TProgress> update)
        {
            lock (gate)
            {
                if (current != authorization)
                {
                    return false;
                }
                update(progress);
                return true;
            }
        }

        internal bool InvalidateIfCurrent(TrackerWriteAuthorization authorization)
        {
            lock (gate)
            {
                if (current != authorization)
                {
                    return false;
                }
                generation++;
                pending = null;
                current = null;
                progress = emptyProgress();
                return true;
            }
        }

        internal void Reset()
        {
            lock (gate)
            {
                generation++;
                pending = null;
                current = null;
                progress = emptyProgress();
            }
        }
    }

    internal readonly record struct TrackerEndpointSnapshot<TClient>(
        long Generation,
        string BaseUrl,
        bool IsOrg,
        TClient Client)
        where TClient : class;

    internal sealed class TrackerEndpointState<TClient> where TClient : class
    {
        private readonly object gate = new();
        private TrackerEndpointSnapshot<TClient> current;

        internal TrackerEndpointState(string baseUrl, bool isOrg, TClient client)
        {
            current = new(1, baseUrl, isOrg, client);
        }

        internal TrackerEndpointSnapshot<TClient> Snapshot
        {
            get
            {
                lock (gate)
                {
                    return current;
                }
            }
        }

        internal TrackerEndpointSnapshot<TClient> Replace(string baseUrl, bool isOrg, TClient client)
        {
            lock (gate)
            {
                current = new(current.Generation + 1, baseUrl, isOrg, client);
                return current;
            }
        }

        internal bool TryResolve(
            TrackerWriteAuthorization authorization,
            out TrackerEndpointSnapshot<TClient> endpoint)
        {
            lock (gate)
            {
                if (!current.IsOrg || current.Generation != authorization.EndpointGeneration)
                {
                    endpoint = default;
                    return false;
                }
                endpoint = current;
                return true;
            }
        }
    }

    internal static class TrackerCompatibility
    {
        private static readonly Regex RejectedActiveStatePattern = new(
            @"(?:unsupported|unknown|unrecognized|invalid)\s+(?:task\s+)?state\s*[:=]?\s*['"" ]?active|(?:task\s+)?state\s*[:=]?\s*['"" ]?active['"" ]?\s+(?:is\s+)?(?:unsupported|not\s+supported|unknown|unrecognized|invalid)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        internal static bool IsUnsupportedActiveState(HttpStatusCode statusCode, string? responseBody)
        {
            if (statusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
                || string.IsNullOrWhiteSpace(responseBody))
            {
                return false;
            }

            var evidence = responseBody.ToLowerInvariant();
            if (!evidence.Contains("active", StringComparison.Ordinal)
                || !evidence.Contains("state", StringComparison.Ordinal))
            {
                return false;
            }

            return RejectedActiveStatePattern.IsMatch(evidence)
                || (evidence.Contains("invalid", StringComparison.Ordinal)
                    && evidence.Contains("enum", StringComparison.Ordinal)
                    && Regex.IsMatch(evidence, @"(?:enum\W{0,30}active|active\W{0,30}enum)", RegexOptions.IgnoreCase))
                || ((evidence.Contains("expected", StringComparison.Ordinal)
                        || evidence.Contains("allowed", StringComparison.Ordinal)
                        || evidence.Contains("one of", StringComparison.Ordinal)
                        || evidence.Contains("valid values", StringComparison.Ordinal))
                    && evidence.Contains("completed", StringComparison.Ordinal)
                    && evidence.Contains("failed", StringComparison.Ordinal));
        }
    }
}
