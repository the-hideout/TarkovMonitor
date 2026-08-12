using System.Net;
using System.Text.RegularExpressions;

namespace TarkovMonitor
{
    internal readonly record struct TrackerProfileSwitch(
        long Generation,
        string ProfileId,
        ProfileType Mode,
        string Token);

    internal readonly record struct TrackerWriteAuthorization(
        long Generation,
        string ProfileId,
        ProfileType Mode,
        string Token)
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

        internal TrackerProfileSwitch BeginSwitch(Profile profile, string token)
        {
            lock (gate)
            {
                generation++;
                current = null;
                progress = emptyProgress();
                return new(generation, profile.Id, profile.Type, token.Trim());
            }
        }

        internal bool TryActivate(
            TrackerProfileSwitch profileSwitch,
            TProgress loadedProgress,
            out TrackerWriteAuthorization authorization)
        {
            lock (gate)
            {
                if (profileSwitch.Generation != generation
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
                    profileSwitch.Token);
                current = authorization;
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
                return profileSwitch.Generation == generation && current == null;
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
                current = null;
                progress = emptyProgress();
            }
        }
    }

    internal static class TrackerCompatibility
    {
        private static readonly Regex RejectedActiveStatePattern = new(
            @"(?:unsupported|unknown|unrecognized|invalid)\s+(?:task\s+)?state\W{0,20}active|state\W{0,20}active\W{0,80}(?:unsupported|not\s+supported|unknown|unrecognized|invalid)",
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
                    && evidence.Contains("enum", StringComparison.Ordinal))
                || ((evidence.Contains("expected", StringComparison.Ordinal)
                        || evidence.Contains("allowed", StringComparison.Ordinal)
                        || evidence.Contains("one of", StringComparison.Ordinal)
                        || evidence.Contains("valid values", StringComparison.Ordinal))
                    && evidence.Contains("completed", StringComparison.Ordinal)
                    && evidence.Contains("failed", StringComparison.Ordinal));
        }
    }
}
