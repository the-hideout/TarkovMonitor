using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TarkovMonitor
{
    internal readonly record struct TaskLifecycleKey(string ProfileId, ProfileType Mode, string TaskId);

    internal sealed record TaskLifecycleEvent(
        DateTimeOffset Timestamp,
        string ProfileId,
        ProfileType Mode,
        string TaskId,
        TaskStatus Status,
        string Identity);

    internal sealed record TaskLifecycleLogSource(string Folder, string Path, string Contents);

    internal sealed class TaskLifecycleReplayResult
    {
        public List<TaskLifecycleEvent> Events { get; } = new();
        public int DuplicateCount { get; internal set; }
        public int UnknownIdentityCount { get; internal set; }

        public Dictionary<TaskLifecycleKey, TaskLifecycleEvent> Coalesce()
        {
            var result = new Dictionary<TaskLifecycleKey, TaskLifecycleEvent>();
            foreach (var lifecycleEvent in Events.OrderBy(TaskLifecycle.SortKey))
            {
                var key = new TaskLifecycleKey(lifecycleEvent.ProfileId, lifecycleEvent.Mode, lifecycleEvent.TaskId);
                result[key] = lifecycleEvent;
            }
            return result;
        }
    }

    internal static class TaskLifecycle
    {
        private const string TimestampPattern = @"(?<date>^\d{4}-\d{2}-\d{2}) (?<time>\d{2}:\d{2}:\d{2}\.\d{3})(?<tzoffset> [+-]\d{2}:\d{2})?\|";
        private static readonly Regex LogMessagePattern = new(
            @$"{TimestampPattern}(?<message>.+$)\s*(?<json>^{{[\s\S]+?^}})?",
            RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex ModePattern = new(@"Session mode:\s*(?<mode>[^\s|]+)", RegexOptions.Compiled);
        private static readonly Regex ProfilePattern = new(
            @"(?:SelectProfile|SelectedProfile|PrepareSelectedProfileLocally|CompleteSelectedProfile) ProfileId:(?<profileId>\w+) AccountId:(?<accountId>\d+)",
            RegexOptions.Compiled);
        private static readonly Regex VersionPattern = new(
            @"^(?<version>\d+\.\d+\.\d+\.\d+)(?:\.\d+)?\|",
            RegexOptions.Compiled);

        internal static string ToTrackerState(TaskStatus status) => status switch
        {
            TaskStatus.Started => "active",
            TaskStatus.Failed => "failed",
            TaskStatus.Finished => "completed",
            TaskStatus.None => "uncompleted",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported task lifecycle state."),
        };

        internal static void ApplyToCache(TrackerTaskProgress storedStatus, TaskStatus status)
        {
            switch (status)
            {
                case TaskStatus.Started:
                    storedStatus.active = true;
                    storedStatus.complete = false;
                    storedStatus.failed = false;
                    storedStatus.invalid = false;
                    break;
                case TaskStatus.Failed:
                    storedStatus.active = false;
                    // Tracker preserves failed as completed + failed.
                    storedStatus.complete = true;
                    storedStatus.failed = true;
                    storedStatus.invalid = false;
                    break;
                case TaskStatus.Finished:
                    storedStatus.active = false;
                    storedStatus.complete = true;
                    storedStatus.failed = false;
                    storedStatus.invalid = false;
                    break;
                case TaskStatus.None:
                    storedStatus.active = false;
                    storedStatus.complete = false;
                    storedStatus.failed = false;
                    storedStatus.invalid = false;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported task lifecycle state.");
            }
        }

        internal static bool CacheMatches(TrackerTaskProgress? storedStatus, TaskStatus status)
        {
            if (storedStatus == null)
            {
                return status == TaskStatus.None;
            }
            return status switch
            {
                TaskStatus.Started => storedStatus.active == true && !storedStatus.complete && !storedStatus.failed && !storedStatus.invalid,
                TaskStatus.Failed => storedStatus.active != true && storedStatus.complete && storedStatus.failed && !storedStatus.invalid,
                TaskStatus.Finished => storedStatus.active != true && storedStatus.complete && !storedStatus.failed && !storedStatus.invalid,
                TaskStatus.None => storedStatus.active == false && !storedStatus.complete && !storedStatus.failed && !storedStatus.invalid,
                _ => false,
            };
        }

        internal static bool ShouldDispatch(Profile profile, bool validToken, string currentProfileId, string token)
        {
            if (!validToken || string.IsNullOrWhiteSpace(profile.Id)
                || profile.Id != currentProfileId || !TrackerTokenFormat.IsSupportedOrgToken(token))
            {
                return false;
            }
            return TrackerTokenFormat.MatchesMode(token, profile.Type);
        }

        internal static TaskLifecycleReplayResult Replay(IEnumerable<TaskLifecycleLogSource> sources, DateTimeOffset notBefore)
        {
            var records = ParseRecords(sources, out var duplicateCount);
            var result = new TaskLifecycleReplayResult { DuplicateCount = duplicateCount };
            var identities = new Dictionary<string, IdentityState>(StringComparer.OrdinalIgnoreCase);
            var seenLifecycleEvents = new HashSet<string>(StringComparer.Ordinal);

            foreach (var record in records.OrderBy(SortKey))
            {
                if (!identities.TryGetValue(record.Folder, out var identity))
                {
                    identity = new IdentityState();
                    identities.Add(record.Folder, identity);
                }
                if (record.Kind == RecordKind.Mode)
                {
                    identity.Mode = record.Mode;
                    identity.ProfileId = string.Empty;
                    identity.Valid = false;
                    continue;
                }
                if (record.Kind == RecordKind.Profile)
                {
                    if (identity.Valid && identity.ProfileId == record.ProfileId)
                    {
                        continue;
                    }
                    identity.ProfileId = record.ProfileId;
                    identity.Valid = identity.Mode != ProfileType.Unknown
                        && !string.IsNullOrWhiteSpace(record.ProfileId);
                    continue;
                }
                if (record.Timestamp < notBefore)
                {
                    continue;
                }
                if (!identity.Valid || identity.Mode == ProfileType.Unknown || string.IsNullOrWhiteSpace(identity.ProfileId))
                {
                    result.UnknownIdentityCount++;
                    continue;
                }
                if (!seenLifecycleEvents.Add(record.Identity))
                {
                    result.DuplicateCount++;
                    continue;
                }
                result.Events.Add(new TaskLifecycleEvent(
                    record.Timestamp, identity.ProfileId, identity.Mode, record.TaskId, record.Status, record.Identity));
            }
            result.Events.Sort((left, right) => SortKey(left).CompareTo(SortKey(right)));
            return result;
        }

        internal static List<LogDetails> GetLogDetails(IEnumerable<TaskLifecycleLogSource> sources)
        {
            var records = ParseRecords(sources, out _);
            var details = new List<LogDetails>();
            var identities = new Dictionary<string, IdentityState>(StringComparer.OrdinalIgnoreCase);

            foreach (var record in records.OrderBy(SortKey))
            {
                if (!identities.TryGetValue(record.Folder, out var identity))
                {
                    identity = new IdentityState();
                    identities.Add(record.Folder, identity);
                }
                if (record.Kind == RecordKind.Mode)
                {
                    identity.Mode = record.Mode;
                    identity.ProfileId = string.Empty;
                    identity.Valid = false;
                    continue;
                }
                if (record.Kind != RecordKind.Profile)
                {
                    continue;
                }
                if (identity.Valid && identity.ProfileId == record.ProfileId)
                {
                    continue;
                }
                identity.ProfileId = record.ProfileId;
                identity.Valid = identity.Mode != ProfileType.Unknown
                    && !string.IsNullOrWhiteSpace(record.ProfileId);
                if (!identity.Valid)
                {
                    continue;
                }
                details.Add(new LogDetails
                {
                    Profile = new Profile { Id = record.ProfileId, Type = identity.Mode, AccountId = record.AccountId },
                    AccountId = int.TryParse(record.AccountId, out var accountId) ? accountId : 0,
                    Date = record.Timestamp.LocalDateTime,
                    Version = record.Version,
                    Folder = record.Folder,
                });
            }
            return details;
        }

        internal static DateTimeOffset ToLocalOffset(DateTime dateTime)
        {
            var unspecified = DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified);
            return new DateTimeOffset(unspecified, TimeZoneInfo.Local.GetUtcOffset(unspecified));
        }

        internal static (long Timestamp, int Status, string Identity) SortKey(TaskLifecycleEvent lifecycleEvent) =>
            (lifecycleEvent.Timestamp.UtcTicks, StatusRank(lifecycleEvent.Status), lifecycleEvent.Identity);

        private static List<RawRecord> ParseRecords(IEnumerable<TaskLifecycleLogSource> sources, out int duplicateCount)
        {
            var records = new List<RawRecord>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var duplicates = 0;

            foreach (var source in sources.OrderBy(source => source.Path, StringComparer.OrdinalIgnoreCase))
            {
                var ordinal = 0;
                foreach (Match match in LogMessagePattern.Matches(source.Contents))
                {
                    ordinal++;
                    if (!TryParseTimestamp(match, out var timestamp))
                    {
                        continue;
                    }
                    var message = match.Groups["message"].Value.TrimEnd('\r');
                    var version = ParseVersion(message);
                    if (message.Contains("Session mode:", StringComparison.Ordinal))
                    {
                        var modeMatch = ModePattern.Match(message);
                        var rawMode = modeMatch.Success ? modeMatch.Groups["mode"].Value : "(missing)";
                        Add(new RawRecord(timestamp, source.Folder, source.Path, ordinal, RecordKind.Mode,
                            ParseMode(rawMode), string.Empty, string.Empty, string.Empty, TaskStatus.None, version,
                            StableHash($"mode|{source.Folder}|{timestamp.UtcTicks}|{rawMode}")));
                        continue;
                    }
                    var profileMatch = ProfilePattern.Match(message);
                    if (profileMatch.Success)
                    {
                        var profileId = profileMatch.Groups["profileId"].Value;
                        var accountId = profileMatch.Groups["accountId"].Value;
                        Add(new RawRecord(timestamp, source.Folder, source.Path, ordinal, RecordKind.Profile,
                            ProfileType.Unknown, profileId, accountId, string.Empty, TaskStatus.None, version,
                            StableHash($"profile|{source.Folder}|{timestamp.UtcTicks}|{profileId}|{accountId}")));
                        continue;
                    }
                    if (!message.Contains("Got notification | ChatMessageReceived", StringComparison.Ordinal)
                        || !match.Groups["json"].Success
                        || !TryParseLifecycle(match.Groups["json"].Value, timestamp,
                            out var taskId, out var status, out var identity))
                    {
                        continue;
                    }
                    Add(new RawRecord(timestamp, source.Folder, source.Path, ordinal, RecordKind.Lifecycle,
                        ProfileType.Unknown, string.Empty, string.Empty, taskId, status, version, identity));
                }
            }
            duplicateCount = duplicates;
            return records;

            void Add(RawRecord record)
            {
                if (record.Kind == RecordKind.Lifecycle)
                {
                    records.Add(record);
                    return;
                }
                if (!identities.Add(record.Identity))
                {
                    duplicates++;
                    return;
                }
                records.Add(record);
            }
        }

        private static bool TryParseLifecycle(
            string json,
            DateTimeOffset timestamp,
            out string taskId,
            out TaskStatus status,
            out string identity)
        {
            taskId = string.Empty;
            status = TaskStatus.None;
            identity = string.Empty;
            try
            {
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("message", out var message)
                    || !message.TryGetProperty("type", out var typeElement)
                    || !TryReadMessageType(typeElement, out var messageType)
                    || messageType < MessageType.TaskStarted
                    || messageType > MessageType.TaskFinished
                    || !message.TryGetProperty("templateId", out var templateElement))
                {
                    return false;
                }
                taskId = (templateElement.GetString() ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(taskId))
                {
                    return false;
                }
                status = (TaskStatus)messageType;
                var stableId = FindStableId(document.RootElement) ?? FindStableId(message);
                if (!string.IsNullOrWhiteSpace(stableId))
                {
                    identity = $"event:{stableId}";
                    return true;
                }
                identity = StableHash(
                    $"lifecycle|{timestamp.UtcTicks}|{taskId}|{(int)status}|{JsonSerializer.Serialize(document.RootElement)}");
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryReadMessageType(JsonElement element, out MessageType messageType)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var numericType))
            {
                messageType = (MessageType)numericType;
                return true;
            }
            if (element.ValueKind == JsonValueKind.String
                && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numericType))
            {
                messageType = (MessageType)numericType;
                return true;
            }
            messageType = default;
            return false;
        }

        private static string? FindStableId(JsonElement element)
        {
            foreach (var propertyName in new[] { "_id", "id", "messageId" })
            {
                if (element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty(propertyName, out var property)
                    && property.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.GetString()))
                {
                    return property.GetString();
                }
            }
            return null;
        }

        private static bool TryParseTimestamp(Match match, out DateTimeOffset timestamp)
        {
            var value = match.Groups["date"].Value + " " + match.Groups["time"].Value;
            if (match.Groups["tzoffset"].Success)
            {
                value += match.Groups["tzoffset"].Value;
                return DateTimeOffset.TryParseExact(value, "yyyy-MM-dd HH:mm:ss.fff zzz",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
            }
            if (DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var localTime))
            {
                timestamp = ToLocalOffset(localTime);
                return true;
            }
            timestamp = default;
            return false;
        }

        private static Version ParseVersion(string message)
        {
            var match = VersionPattern.Match(message);
            return match.Success && Version.TryParse(match.Groups["version"].Value, out var version)
                ? version
                : new Version(0, 0);
        }

        private static ProfileType ParseMode(string rawMode) =>
            ProfileIdentity.TryParseMode(rawMode, out var mode) ? mode : ProfileType.Unknown;

        private static (long Timestamp, int Kind, string Task, int Status, string Identity, string Source, int Ordinal)
            SortKey(RawRecord record) =>
            (record.Timestamp.UtcTicks, (int)record.Kind, record.TaskId, StatusRank(record.Status),
                record.Identity, record.SourcePath, record.SourceOrdinal);

        private static int StatusRank(TaskStatus status) => status switch
        {
            TaskStatus.None => 0,
            TaskStatus.Started => 1,
            TaskStatus.Failed => 2,
            TaskStatus.Finished => 3,
            _ => 4,
        };

        private static string StableHash(string value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

        private enum RecordKind
        {
            Mode = 0,
            Profile = 1,
            Lifecycle = 2,
        }

        private sealed record RawRecord(
            DateTimeOffset Timestamp,
            string Folder,
            string SourcePath,
            int SourceOrdinal,
            RecordKind Kind,
            ProfileType Mode,
            string ProfileId,
            string AccountId,
            string TaskId,
            TaskStatus Status,
            Version Version,
            string Identity);

        private sealed class IdentityState
        {
            public ProfileType Mode { get; set; } = ProfileType.Unknown;
            public string ProfileId { get; set; } = string.Empty;
            public bool Valid { get; set; }
        }

    }
}
