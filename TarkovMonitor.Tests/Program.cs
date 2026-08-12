using System.Text.Json;
using TarkovMonitor;
using TaskStatus = TarkovMonitor.TaskStatus;

var tests = new (string Name, Action Run)[]
{
    ("start maps to active", StartMapsToActive),
    ("dispatch is unconditional after guards", DispatchIsUnconditionalAfterGuards),
    ("response active is nullable", ResponseActiveIsNullable),
    ("cache lifecycle truth table", CacheLifecycleTruthTable),
    ("start then finish coalesces terminal", StartThenFinish),
    ("fail then restart coalesces active", FailThenRestart),
    ("rotated duplicates are removed", DuplicatesAreRemoved),
    ("same-time terminal ordering is deterministic", SameTimeOrdering),
    ("profile and mode boundaries stay isolated", MixedProfileModeBoundaries),
    ("unknown identity fails closed", UnknownIdentityFailsClosed),
    ("coalescing keeps batch quota bounded", CoalescedBatchQuota),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {test.Name}: {exception.Message}");
    }
}
foreach (var failure in failures)
{
    Console.Error.WriteLine(failure);
}
if (failures.Count > 0)
{
    Environment.Exit(1);
}
Console.WriteLine($"All {tests.Length} lifecycle tests passed.");

static void StartMapsToActive()
{
    Equal("active", TaskLifecycle.ToTrackerState(TaskStatus.Started));
    Equal("uncompleted", TaskLifecycle.ToTrackerState(TaskStatus.None));
}

static void DispatchIsUnconditionalAfterGuards()
{
    var profile = new Profile { Id = "profilea", Type = ProfileType.Regular };
    True(TaskLifecycle.ShouldDispatch(profile, true, "profilea", "PVP_0123456789abcdef01"));
    False(TaskLifecycle.ShouldDispatch(profile, false, "profilea", "PVP_0123456789abcdef01"));
    False(TaskLifecycle.ShouldDispatch(profile, true, "profileb", "PVP_0123456789abcdef01"));
    False(TaskLifecycle.ShouldDispatch(profile, true, "profilea", "PVE_0123456789abcdef01"));
    False(TaskLifecycle.ShouldDispatch(
        new Profile { Id = "profilea", Type = ProfileType.Unknown },
        true, "profilea", "PVP_0123456789abcdef01"));
}

static void ResponseActiveIsNullable()
{
    var legacy = JsonSerializer.Deserialize<TarkovTracker.ProgressResponseTask>(
        "{\"id\":\"a\",\"complete\":false}")!;
    var active = JsonSerializer.Deserialize<TarkovTracker.ProgressResponseTask>(
        "{\"id\":\"a\",\"active\":true}")!;
    Equal<bool?>(null, legacy.active);
    Equal<bool?>(true, active.active);
    legacy.complete = true;
    True(TaskLifecycle.CacheMatches(legacy, TaskStatus.Finished));
}

static void CacheLifecycleTruthTable()
{
    AssertCache(TaskStatus.Started, active: true, complete: false, failed: false, invalid: false);
    AssertCache(TaskStatus.Failed, active: false, complete: true, failed: true, invalid: false);
    AssertCache(TaskStatus.Finished, active: false, complete: true, failed: false, invalid: false);
    AssertCache(TaskStatus.None, active: false, complete: false, failed: false, invalid: false);
}

static void StartThenFinish()
{
    var result = TaskLifecycle.Replay(
        new[]
        {
            new TaskLifecycleLogSource("folder", "m-identity.log",
                Identity("2026-01-01 00:00:00.000", "Regular", "profilea", "1")),
            new TaskLifecycleLogSource("folder", "z-start.log",
                Lifecycle("2026-01-01 00:00:01.000", 10, "task-a", "start")),
            new TaskLifecycleLogSource("folder", "a-finish.log",
                Lifecycle("2026-01-01 00:00:02.000", 12, "task-a", "finish")),
        },
        DateTimeOffset.MinValue);
    Equal(TaskStatus.Finished, result.Coalesce().Single().Value.Status);
}

static void FailThenRestart()
{
    var result = Replay(
        Identity("2026-01-01 00:00:00.000", "Regular", "profilea", "1"),
        Lifecycle("2026-01-01 00:00:01.000", 11, "task-a", "fail"),
        Lifecycle("2026-01-01 00:00:02.000", 10, "task-a", "restart"));
    Equal(TaskStatus.Started, result.Coalesce().Single().Value.Status);
}

static void DuplicatesAreRemoved()
{
    var identity = Identity("2026-01-01 00:00:00.000", "Regular", "profilea", "1");
    var lifecycle = Lifecycle("2026-01-01 00:00:01.000", 10, "task-a", "same-id");
    var result = TaskLifecycle.Replay(
        new[]
        {
            new TaskLifecycleLogSource("a-unknown-folder", "a-notifications.log", lifecycle),
            new TaskLifecycleLogSource("folder", "application.log", identity),
            new TaskLifecycleLogSource("folder", "notifications.log", lifecycle),
            new TaskLifecycleLogSource("folder", "notifications_000.log", lifecycle),
        },
        DateTimeOffset.MinValue);
    Equal(1, result.Events.Count);
    True(result.DuplicateCount >= 1);
}

static void SameTimeOrdering()
{
    var result = Replay(
        Identity("2026-01-01 00:00:00.000", "Regular", "profilea", "1"),
        Lifecycle("2026-01-01 00:00:01.000", 12, "task-a", "z-finish"),
        Lifecycle("2026-01-01 00:00:01.000", 10, "task-a", "a-start"));
    Equal(TaskStatus.Finished, result.Coalesce().Single().Value.Status);
}

static void MixedProfileModeBoundaries()
{
    var result = Replay(
        Identity("2026-01-01 00:00:00.000", "Regular", "profilea", "1"),
        Lifecycle("2026-01-01 00:00:01.000", 10, "task-a", "regular-start"),
        Identity("2026-01-01 00:00:02.000", "PVE", "profileb", "2"),
        Lifecycle("2026-01-01 00:00:03.000", 12, "task-a", "pve-finish"));
    var coalesced = result.Coalesce();
    Equal(2, coalesced.Count);
    Equal(TaskStatus.Started,
        coalesced[new TaskLifecycleKey("profilea", ProfileType.Regular, "task-a")].Status);
    Equal(TaskStatus.Finished,
        coalesced[new TaskLifecycleKey("profileb", ProfileType.PVE, "task-a")].Status);
}

static void UnknownIdentityFailsClosed()
{
    var result = Replay(
        Lifecycle("2026-01-01 00:00:00.000", 10, "task-a", "no-identity"),
        Identity("2026-01-01 00:00:01.000", "future-mode", "profilea", "1"),
        Lifecycle("2026-01-01 00:00:02.000", 12, "task-a", "unknown-mode"));
    Equal(0, result.Events.Count);
    Equal(2, result.UnknownIdentityCount);
}

static void CoalescedBatchQuota()
{
    var logs = new List<string>
    {
        Identity("2026-01-01 00:00:00.000", "PvpSeason", "profilea", "1"),
    };
    for (var index = 0; index < 100; index++)
    {
        logs.Add(Lifecycle(
            $"2026-01-01 00:00:{index % 60:00}.{index:000}",
            index % 2 == 0 ? 10 : 11,
            $"task-{index % 3}",
            $"event-{index}"));
    }
    Equal(3, Replay(logs.ToArray()).Coalesce().Count);
}

static TaskLifecycleReplayResult Replay(params string[] contents) =>
    TaskLifecycle.Replay(
        contents.Select((value, index) => new TaskLifecycleLogSource("folder", $"{index:000}.log", value)),
        DateTimeOffset.MinValue);

static string Identity(string timestamp, string mode, string profile, string account) =>
    Line(timestamp, $"0.16.0.0.1|Info|application|Session mode: {mode}")
    + Line(timestamp, $"0.16.0.0.1|Info|application|SelectProfile ProfileId:{profile} AccountId:{account}");

static string Lifecycle(string timestamp, int type, string task, string id) =>
    Line(
        timestamp,
        "0.16.0.0.1|Info|notifications|Got notification | ChatMessageReceived",
        $"{{\n  \"message\": {{\"_id\":\"{id}\",\"type\":{type},\"templateId\":\"{task} 0\"}}\n}}");

static string Line(string timestamp, string message, string? json = null) =>
    $"{timestamp} +00:00|{message}\n{(json == null ? string.Empty : json + "\n")}";

static void AssertCache(TaskStatus status, bool active, bool complete, bool failed, bool invalid)
{
    var actual = new TarkovTracker.ProgressResponseTask
    {
        id = "task-a",
        active = !active,
        complete = !complete,
        failed = !failed,
        invalid = !invalid,
    };
    TaskLifecycle.ApplyToCache(actual, status);
    Equal<bool?>(active, actual.active);
    Equal(complete, actual.complete);
    Equal(failed, actual.failed);
    Equal(invalid, actual.invalid);
    True(TaskLifecycle.CacheMatches(actual, status));
}

static void True(bool condition)
{
    if (!condition) throw new InvalidOperationException("Expected true.");
}

static void False(bool condition)
{
    if (condition) throw new InvalidOperationException("Expected false.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
