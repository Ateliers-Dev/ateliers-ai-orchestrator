using System.Net;
using System.Text.Json;
using Ateliers.Slack;
using Xunit;

namespace Ateliers.Slack.UnitTests;

public sealed class SlackLifecycleNotificationDispatcherTests
{
    private static readonly string RunId = Guid.Parse("4e8f9f1f-028d-4a29-9227-8634b56a79bd").ToString("N");
    private const string TokenPrefix = "xoxb-lifecycle-test-";

    [Fact]
    public async Task TrySend_RoutesByRoleAndSendsOnlyFixedStructuredFields()
    {
        using var files = new LifecycleSettingsFiles();
        var handlers = new Dictionary<string, RecordingHandlerBase>(StringComparer.Ordinal);
        using var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(files.ConfigurationPath, files.SecretsPath,
            botId => handlers[botId] = new SuccessRecordingHandler(_ => SuccessResponse(botId)));

        Assert.NotNull(dispatcher);
        foreach (var (role, botId) in new[]
        {
            ("Orchestrator", "orchestrator"), ("Implementer", "implementer"), ("Reviewer", "reviewer")
        })
        {
            Assert.Equal(SlackNotificationSendResult.Sent, await dispatcher!.TrySendAsync(RunId, role, "started", "running", "Implementing",
                new DateTimeOffset(2026, 11, 13, 2, 3, 4, TimeSpan.Zero)));
            var handler = handlers[botId];
            Assert.Single(handler.Requests);
            Assert.Equal($"Bearer {TokenPrefix}{botId}", handler.Requests[0].Headers.Authorization!.ToString());
            using var payload = JsonDocument.Parse(handler.RequestBodies[0]!);
            Assert.Equal($"C_{botId}", payload.RootElement.GetProperty("channel").GetString());
            var message = payload.RootElement.GetProperty("text").GetString()!;
            Assert.Equal($"[Ateliers AI] run={RunId} role={role} event=started status=running state=Implementing at=2026-11-13T02:03:04.0000000+00:00", message);
            Assert.DoesNotContain(TokenPrefix, message, StringComparison.Ordinal);
            Assert.DoesNotContain("prompt", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("response", message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(SlackNotificationSendResult.Skipped,
            await dispatcher!.TrySendAsync(RunId, "Orchestrator", "completed", "needs_human_input", "NeedsHumanInput", DateTimeOffset.UtcNow));
        Assert.Equal(0, dispatcher.FailedSends);
        Assert.Single(handlers["orchestrator"].Requests);
    }

    [Fact]
    public void TryCreate_ReturnsNullWhenLocalConfigurationIsNotPresent()
    {
        using var temp = new TemporaryDirectory();

        var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(
            Path.Combine(temp.Path, "slack.json"), Path.Combine(temp.Path, "slack.secrets.json"), out var warning);

        Assert.Null(dispatcher);
        Assert.Null(warning);
    }

    [Fact]
    public void TryCreate_WarnsAndSkipsWhenSecretsAreMissing()
    {
        using var files = new LifecycleSettingsFiles();
        File.Delete(files.SecretsPath);

        var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(files.ConfigurationPath, files.SecretsPath, out var warning);

        Assert.Null(dispatcher);
        Assert.Contains("secrets", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TokenPrefix, warning!, StringComparison.Ordinal);
        Assert.DoesNotContain(files.SecretsPath, warning!, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_WarnsAndSkipsAllNotificationsWhenAnyBotDefinitionIsMissing()
    {
        using var files = new LifecycleSettingsFiles();
        File.WriteAllText(files.ConfigurationPath, """{"bots":[{"id":"orchestrator","name":"Orchestrator","botUserId":"U1","channelId":"C1","enabled":true},{"id":"implementer","name":"Implementer","botUserId":"U2","channelId":"C1","enabled":true}]}""");

        var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(files.ConfigurationPath, files.SecretsPath, out var warning);

        Assert.Null(dispatcher);
        Assert.Contains("Bot", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenPrefix, warning!, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_WarnsAndSkipsAllNotificationsWhenAnyBotTokenIsMissing()
    {
        using var files = new LifecycleSettingsFiles();
        File.WriteAllText(files.SecretsPath, """{"tokens":{"orchestrator":"test-token","implementer":"test-token"}}""");

        var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(files.ConfigurationPath, files.SecretsPath, out var warning);

        Assert.Null(dispatcher);
        Assert.Contains("secrets", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test-token", warning!, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_WarnsAndSkipsForMalformedSettingsWithoutExposingContents()
    {
        using var files = new LifecycleSettingsFiles();
        File.WriteAllText(files.ConfigurationPath, $"invalid {TokenPrefix}not-a-real-token");

        var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(files.ConfigurationPath, files.SecretsPath, out var warning);

        Assert.Null(dispatcher);
        Assert.NotNull(warning);
        Assert.DoesNotContain(TokenPrefix, warning!, StringComparison.Ordinal);
        Assert.DoesNotContain(files.ConfigurationPath, warning!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrySend_FailureIsContainedAndDoesNotLeakTokenOrSlackResponse()
    {
        using var files = new LifecycleSettingsFiles();
        var handlers = new Dictionary<string, RecordingHandlerBase>(StringComparer.Ordinal);
        using var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(files.ConfigurationPath, files.SecretsPath,
            botId => handlers[botId] = new SuccessRecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent($"{TokenPrefix}{botId} secret-response")
            }));

        Assert.NotNull(dispatcher);
        var result = await dispatcher!.TrySendAsync(RunId, "Reviewer", "failed", "failed", "NeedsHumanInput", DateTimeOffset.UtcNow);

        Assert.Equal(SlackNotificationSendResult.Failed, result);
        Assert.Equal(1, dispatcher.FailedSends);
        Assert.Single(handlers["reviewer"].Requests);
        Assert.DoesNotContain(TokenPrefix, handlers["reviewer"].RequestBodies[0]!, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-response", handlers["reviewer"].RequestBodies[0]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrySend_TimeoutIsContainedAndCountedWithoutRetry()
    {
        using var files = new LifecycleSettingsFiles();
        var handlers = new Dictionary<string, RecordingHandlerBase>(StringComparer.Ordinal);
        using var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(files.ConfigurationPath, files.SecretsPath,
            botId => handlers[botId] = new DelayedRecordingHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return SuccessResponse(botId);
            }),
            TimeSpan.FromMilliseconds(50));

        Assert.NotNull(dispatcher);
        var result = await dispatcher!.TrySendAsync(RunId, "Implementer", "started", "running", "Implementing", DateTimeOffset.UtcNow);

        Assert.Equal(SlackNotificationSendResult.Failed, result);
        Assert.Equal(1, dispatcher.FailedSends);
        Assert.Single(handlers["implementer"].Requests);
    }

    [Fact]
    public async Task TrySend_DoesNotSendForUnknownRoleOrUnstructuredValues()
    {
        using var files = new LifecycleSettingsFiles();
        var handlers = new Dictionary<string, RecordingHandlerBase>(StringComparer.Ordinal);
        using var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(files.ConfigurationPath, files.SecretsPath,
            botId => handlers[botId] = new SuccessRecordingHandler(_ => SuccessResponse(botId)));

        Assert.NotNull(dispatcher);
        Assert.Equal(SlackNotificationSendResult.Failed,
            await dispatcher!.TrySendAsync(RunId, "Unknown", "started", "running", "Implementing", DateTimeOffset.UtcNow));
        Assert.Equal(SlackNotificationSendResult.Failed,
            await dispatcher.TrySendAsync("run-with-secret", "Orchestrator", "started", "running", "Implementing", DateTimeOffset.UtcNow));
        Assert.Equal(SlackNotificationSendResult.Failed,
            await dispatcher.TrySendAsync(RunId, "Orchestrator", "started", "running", "prompt-secret", DateTimeOffset.UtcNow));
        Assert.Equal(3, dispatcher.FailedSends);
        Assert.All(handlers.Values, handler => Assert.Empty(handler.Requests));
    }

    private static HttpResponseMessage SuccessResponse(string botId) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"{{\"ok\":true,\"channel\":\"C_{botId}\",\"ts\":\"1700000000.000001\"}}")
    };

    private sealed class LifecycleSettingsFiles : IDisposable
    {
        private readonly TemporaryDirectory directory = new();
        private static readonly string[] BotIds = ["orchestrator", "implementer", "reviewer"];

        public string ConfigurationPath { get; }
        public string SecretsPath { get; }

        public LifecycleSettingsFiles()
        {
            ConfigurationPath = Path.Combine(directory.Path, "slack.json");
            SecretsPath = Path.Combine(directory.Path, "slack.secrets.json");
            var bots = BotIds.Select(botId => new
            {
                id = botId,
                name = $"{botId} bot",
                botUserId = $"U_{botId}",
                channelId = $"C_{botId}",
                enabled = true
            });
            File.WriteAllText(ConfigurationPath, JsonSerializer.Serialize(new { bots }));
            File.WriteAllText(SecretsPath, JsonSerializer.Serialize(new
            {
                tokens = BotIds.ToDictionary(botId => botId, botId => $"{TokenPrefix}{botId}", StringComparer.Ordinal)
            }));
        }

        public void Dispose() => directory.Dispose();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ateliers-slack-lifecycle-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class SuccessRecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : RecordingHandlerBase
    {
        protected override Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class DelayedRecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : RecordingHandlerBase
    {
        protected override Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }

    private abstract class RecordingHandlerBase : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            return await RespondAsync(request, cancellationToken);
        }

        protected abstract Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken);
    }
}
