using System.Net;
using System.Text.Json;
using Newtonsoft.Json;
using Xunit;

namespace TarkovMonitor.Tests;

public sealed class SyntheticSandboxTests
{
    [Fact]
    public void PersistenceFailureDoesNotTurnDiagnosticsIntoAnApplicationFailure()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"TarkovMonitor-diagnostics-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(filePath, "This path is intentionally a file, not a directory.");

            var service = new DiagnosticsService(filePath);
            var exception = Record.Exception(() => service.Capture(
                new DiagnosticContext("TM-TEST-PERSIST-001", "Persist", "Test", "Write", "Write failed."),
                new IOException("Synthetic write failure")));

            Assert.Null(exception);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public void MissingApiDataProducesAnActionableErrorInsteadOfANullReference()
    {
        var exception = Assert.Throws<InvalidDataException>(() => TarkovDev.RequireApiData<TarkovDev.TasksResponse>(null, "regular/tasks"));

        Assert.Contains("regular/tasks", exception.Message, StringComparison.Ordinal);
        Assert.Contains("did not contain data", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticKeysDoNotReintroduceSensitiveOperationText()
    {
        var diagnosticsDirectory = Path.Combine(Path.GetTempPath(), "TarkovMonitor-DiagnosticsTests", Guid.NewGuid().ToString("N"));
        try
        {
            var snapshot = new DiagnosticsService(diagnosticsDirectory).Capture(
                new DiagnosticContext("TM-TEST-KEY-001", "token=secret-operation", "Test", "Capture", "Failure."));

            Assert.DoesNotContain("secret-operation", snapshot.DiagnosticKey, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", snapshot.DiagnosticKey, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(diagnosticsDirectory))
            {
                Directory.Delete(diagnosticsDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void LevelLookupDoesNotCrashBeforeOrWithoutLevelData()
    {
        Assert.Equal(0, TarkovDev.GetLevel(Array.Empty<TarkovDev.PlayerLevel>(), 100));
        Assert.Equal(1, TarkovDev.GetLevel(
            new[] { new TarkovDev.PlayerLevel { level = 1, exp = 100 } },
            10));
        Assert.Equal(1, TarkovDev.GetLevel(
            new[]
            {
                new TarkovDev.PlayerLevel { level = 1, exp = 100 },
                new TarkovDev.PlayerLevel { level = 2, exp = 200 },
            },
            150));
    }

    [Fact]
    public void DiagnosticAndAnalyticsFilesRotateBeforeTheyGrowWithoutBound()
    {
        var diagnosticsDirectory = Path.Combine(Path.GetTempPath(), "TarkovMonitor-DiagnosticsTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(diagnosticsDirectory);
            File.WriteAllText(Path.Combine(diagnosticsDirectory, "diagnostics.jsonl"), new string('d', 1_000_001));
            File.WriteAllText(Path.Combine(diagnosticsDirectory, "analytics.jsonl"), new string('a', 1_000_001));

            var service = new DiagnosticsService(diagnosticsDirectory);
            service.Capture(new DiagnosticContext("TM-TEST-ROTATE-001", "Rotate", "Test", "Write", "Rotation test."));

            Assert.True(File.Exists(Path.Combine(diagnosticsDirectory, "diagnostics.jsonl")));
            Assert.True(File.Exists(Path.Combine(diagnosticsDirectory, "diagnostics.jsonl.1")));
            Assert.True(File.Exists(Path.Combine(diagnosticsDirectory, "analytics.jsonl")));
            Assert.True(File.Exists(Path.Combine(diagnosticsDirectory, "analytics.jsonl.1")));
        }
        finally
        {
            if (Directory.Exists(diagnosticsDirectory))
            {
                Directory.Delete(diagnosticsDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void RepeatedFailuresStayShortButRetainTheLatestRawDiagnosticForCopy()
    {
        var diagnosticsDirectory = Path.Combine(Path.GetTempPath(), "TarkovMonitor-DiagnosticsTests", Guid.NewGuid().ToString("N"));
        try
        {
            var log = new MessageLog(new DiagnosticsService(diagnosticsDirectory));
            var events = new List<NewLogMessageArgs>();
            log.newMessage += (_, args) => events.Add(args);

            log.AddException(
                "The request failed. Copy diagnostics for details.",
                "TM-TEST-REPEAT-001",
                "Request",
                new HttpRequestException("Synthetic TLS failure", null, HttpStatusCode.BadGateway),
                "TestService",
                "Transport");
            log.AddException(
                "The request failed. Copy diagnostics for details.",
                "TM-TEST-REPEAT-001",
                "Request",
                new HttpRequestException("Synthetic TLS failure", null, HttpStatusCode.BadGateway),
                "TestService",
                "Transport");

            var message = Assert.Single(log.Messages);
            Assert.Contains("repeated 2 times", message.Message, StringComparison.Ordinal);
            Assert.Equal(2, message.DiagnosticOccurrenceCount);
            Assert.Equal(2, events.Count);
            Assert.False(events[0].IsRepeat);
            Assert.True(events[1].IsRepeat);
            Assert.Contains("Exception chain:", message.DiagnosticText, StringComparison.Ordinal);

            var copyButton = Assert.Single(message.Buttons, button => button.Text == "Copy diagnostics");
            var copyFailure = Record.Exception(() => copyButton.OnClick?.Invoke());
            Assert.Null(copyFailure);
        }
        finally
        {
            if (Directory.Exists(diagnosticsDirectory))
            {
                Directory.Delete(diagnosticsDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void MessageLogKeepsRecentMessagesBounded()
    {
        var log = new MessageLog(new DiagnosticsService(Path.Combine(Path.GetTempPath(), "TarkovMonitor-unused")));

        for (var index = 0; index < 250; index++)
        {
            log.AddMessage($"Synthetic message {index}");
        }

        Assert.Equal(200, log.Messages.Count);
        Assert.Equal("Synthetic message 50", log.Messages[0].Message);
        Assert.Equal("Synthetic message 249", log.Messages[^1].Message);
    }

    [Fact]
    public async Task MessageSnapshotsRemainReadableWhileBackgroundMessagesArrive()
    {
        var log = new MessageLog(new DiagnosticsService(Path.Combine(Path.GetTempPath(), "TarkovMonitor-unused")));
        var writer = Task.Run(() =>
        {
            for (var index = 0; index < 500; index++)
            {
                log.AddMessage($"Concurrent message {index}");
            }
        });

        var readFailure = Record.Exception(() =>
        {
            for (var index = 0; index < 500; index++)
            {
                _ = log.Messages.Count;
                _ = log.Messages.ToList();
            }
        });

        await writer;
        Assert.Null(readFailure);
        Assert.Equal(200, log.Messages.Count);
    }

    [Fact]
    public async Task ConcurrentFailuresCollapseSafelyAndKeepOccurrenceCountsAccurate()
    {
        var diagnosticsDirectory = Path.Combine(Path.GetTempPath(), "TarkovMonitor-DiagnosticsTests", Guid.NewGuid().ToString("N"));
        try
        {
            var log = new MessageLog(new DiagnosticsService(diagnosticsDirectory));
            var failures = Enumerable.Range(0, 20).Select(_ => Task.Run(() => log.AddException(
                "The request failed.",
                "TM-TEST-CONCURRENT-001",
                "ConcurrentRequest",
                new HttpRequestException("Synthetic network failure"),
                "TestService",
                "Transport")));

            await Task.WhenAll(failures);

            var message = Assert.Single(log.Messages);
            Assert.Equal(20, message.DiagnosticOccurrenceCount);
        }
        finally
        {
            if (Directory.Exists(diagnosticsDirectory))
            {
                Directory.Delete(diagnosticsDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void NotificationSubscriberFailureDoesNotCrashTheOriginalFailurePath()
    {
        var diagnosticsDirectory = Path.Combine(Path.GetTempPath(), "TarkovMonitor-DiagnosticsTests", Guid.NewGuid().ToString("N"));
        try
        {
            var log = new MessageLog(new DiagnosticsService(diagnosticsDirectory));
            log.newMessage += (_, _) => throw new InvalidOperationException("Synthetic UI subscriber failure");

            var exception = Record.Exception(() => log.AddException(
                "The request failed.",
                "TM-TEST-SUBSCRIBER-001",
                "Request",
                new HttpRequestException("Synthetic network failure"),
                "TestService",
                "Transport"));

            Assert.Null(exception);
            Assert.Single(log.Messages);
            var diagnosticsFile = File.ReadAllText(Path.Combine(diagnosticsDirectory, "diagnostics.jsonl"));
            Assert.Contains("TM-UI-005", diagnosticsFile, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(diagnosticsDirectory))
            {
                Directory.Delete(diagnosticsDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void TarkovDevDtosMatchCurrentTasksMapsItemsAndHideoutDataShapes()
    {
        const string tasksPayload = "{\"data\":{\"tasks\":{\"task-id\":{\"id\":\"task-id\",\"name\":\"Task\",\"normalizedName\":\"task\",\"failConditions\":[]}}}}";
        const string mapsPayload = "{\"data\":{\"maps\":{\"map-id\":{\"id\":\"map-id\",\"name\":\"Map\",\"nameId\":\"map\",\"normalizedName\":\"map\",\"scenePath\":\"scene\"}}}}";
        const string itemsPayload = "{\"data\":{\"items\":{\"item-id\":{\"id\":\"item-id\",\"name\":\"Item\"}},\"playerLevels\":[{\"level\":1,\"exp\":0}],\"settings\":{\"scavCooldownSeconds\":1500}}}";
        const string hideoutPayload = "{\"data\":{\"station-id\":{\"id\":\"station-id\",\"name\":\"Station\",\"normalizedName\":\"station\"}}}";

        using var tasksDocument = JsonDocument.Parse(tasksPayload);
        using var mapsDocument = JsonDocument.Parse(mapsPayload);
        using var itemsDocument = JsonDocument.Parse(itemsPayload);
        using var hideoutDocument = JsonDocument.Parse(hideoutPayload);

        var tasks = tasksDocument.RootElement.GetProperty("data").Deserialize<TarkovDev.TasksResponse>();
        var maps = mapsDocument.RootElement.GetProperty("data").Deserialize<TarkovDev.MapsResponse>();
        var items = itemsDocument.RootElement.GetProperty("data").Deserialize<TarkovDev.ItemsResponse>();
        var hideout = hideoutDocument.RootElement.GetProperty("data").Deserialize<Dictionary<string, TarkovDev.HideoutStation>>();

        Assert.NotNull(tasks);
        Assert.NotNull(maps);
        Assert.NotNull(items);
        Assert.NotNull(hideout);
        Assert.Equal("Task", tasks!.tasks["task-id"].name);
        Assert.Equal("Map", maps!.maps["map-id"].name);
        Assert.False(maps.maps["map-id"].HasGoons());
        Assert.Empty(items!.items["item-id"].types);
        Assert.Equal(1500, items!.settings.scavCooldownSeconds);
        Assert.Empty(hideout!["station-id"].levels);
        Assert.Equal("Station", hideout!["station-id"].name);

        var tasksEnvelope = JsonConvert.DeserializeObject<TarkovDev.JsonApiEnvelope<TarkovDev.TasksResponse>>(tasksPayload);
        var envelopeTasks = TarkovDev.RequireApiData(tasksEnvelope?.data, "regular/tasks");
        Assert.Equal("Task", envelopeTasks.tasks["task-id"].name);

        var diagnosticsDirectory = Path.Combine(Path.GetTempPath(), "TarkovMonitor-DiagnosticsTests", Guid.NewGuid().ToString("N"));
        try
        {
            var snapshot = new DiagnosticsService(diagnosticsDirectory).Capture(
                new DiagnosticContext("TM-TEST-API-001", "Payload", "TarkovDev", "Decode", "Payload decoded."));
            var diagnosticsFile = File.ReadAllText(Path.Combine(diagnosticsDirectory, "diagnostics.jsonl"));
            Assert.Contains(snapshot.DisplayMessage, diagnosticsFile, StringComparison.Ordinal);
            Assert.Contains(snapshot.DiagnosticKey, diagnosticsFile, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(diagnosticsDirectory))
            {
                Directory.Delete(diagnosticsDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void TranslationPathsHandleMissingValuesAndTranslateExistingValues()
    {
        var response = new TarkovDev.TasksResponse
        {
            tasks = new Dictionary<string, TarkovDev.Task>
            {
                ["task-id"] = new TarkovDev.Task
                {
                    id = "task-id",
                    name = null!,
                    normalizedName = "original",
                },
            },
        };

        var translated = TarkovDev.ApplyTranslations(
            response,
            new List<string>
            {
                "$.data.tasks['task-id'].name",
                "$.data.tasks['task-id'].normalizedName",
            },
            new Dictionary<string, string> { ["original"] = "Localized" },
            new Dictionary<string, string>());

        Assert.Null(translated.tasks["task-id"].name);
        Assert.Equal("Localized", translated.tasks["task-id"].normalizedName);
    }
}
