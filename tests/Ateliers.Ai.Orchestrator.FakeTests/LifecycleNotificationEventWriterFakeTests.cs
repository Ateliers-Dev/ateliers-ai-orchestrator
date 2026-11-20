using System.Text.Json;
using Ateliers.Ai.Orchestrator.Cli;
using Ateliers.Ai.Orchestrator.Core;
using Ateliers.Slack;
using Xunit;

namespace Ateliers.Ai.Orchestrator.FakeTests;

[Trait("Category", "Fake")]
public sealed class LifecycleNotificationEventWriterFakeTests
{
    [Fact]
    public async Task Flush_RecordsInFlightAndQueuedNotificationsAsAbandonedExactlyOnce()
    {
        using var temp = new TemporaryDirectory();
        var logPath = Path.Combine(temp.Path, "lifecycle.jsonl");
        var senderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writer = new LifecycleNotificationEventWriter(logPath, notificationsDisabled: false,
            async (_, cancellationToken) =>
            {
                senderStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return SlackNotificationSendResult.Sent;
            });

        writer.TryAppend(Event("event-1"));
        await senderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        writer.TryAppend(Event("event-2"));
        writer.TryAppend(Event("event-3"));

        await writer.FlushSlackNotificationsAsync();

        var records = File.ReadAllLines(logPath).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var results = records.Select(record => record.RootElement)
                .Where(item => item.GetProperty("eventType").GetString() == "slack_delivery_result").ToArray();
            Assert.Equal(3, results.Length);
            Assert.All(results, item => Assert.Equal("abandoned", item.GetProperty("status").GetString()));
            Assert.All(results, item => Assert.Equal("started", item.GetProperty("relatedId").GetString()));
            Assert.Equal(3, results.Select(item => item.GetProperty("runId").GetString()).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(3, writer.FailedSlackSends);
            Assert.Equal(0, writer.SentSlackSends);
            Assert.All(results, item => Assert.DoesNotContain("message body", item.GetRawText(), StringComparison.Ordinal));
        }
        finally
        {
            foreach (var record in records) record.Dispose();
        }
    }

    private static LifecycleEvent Event(string relatedId) => new()
    {
        RunId = Guid.NewGuid().ToString("N"),
        TimestampUtc = DateTimeOffset.UtcNow,
        Role = "Implementer",
        EventType = "started",
        Status = "running",
        State = "Implementing",
        AgentId = "builder",
        AgentName = "Builder",
        SlackBotId = "implementer-bot",
        RelatedId = "message body",
        Summary = "message body must not be copied"
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lifecycle-writer-tests-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
