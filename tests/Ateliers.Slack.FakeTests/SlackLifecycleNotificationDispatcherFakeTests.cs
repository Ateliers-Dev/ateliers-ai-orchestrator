using System.Net;
using System.Text.Json;
using Ateliers.Slack;
using Xunit;

namespace Ateliers.Slack.FakeTests;

public sealed class SlackLifecycleNotificationDispatcherTests
{
    private static readonly string RunId = Guid.Parse("4e8f9f1f-028d-4a29-9227-8634b56a79bd").ToString("N");
    private const string TokenPrefix = "lifecycle-test-token-";

    [Fact]
    public async Task TrySend_UsesRoleBotInSharedRunChannelAndParentThread()
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
            Assert.Equal(role == "Orchestrator" ? 2 : 1, handler.Requests.Count);
            Assert.Equal($"Bearer {TokenPrefix}{botId}", handler.Requests[0].Headers.Authorization!.ToString());
            using var payload = JsonDocument.Parse(handler.RequestBodies[0]!);
            Assert.Equal("C_orchestrator", payload.RootElement.GetProperty("channel").GetString());
            var message = payload.RootElement.GetProperty("text").GetString()!;
            Assert.DoesNotContain("run=", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("role=", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("event=", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("status=", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("state=", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("at=", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(RunId, message, StringComparison.Ordinal);
            Assert.StartsWith("*", message, StringComparison.Ordinal);
            if (role == "Orchestrator") Assert.False(payload.RootElement.TryGetProperty("thread_ts", out _));
            else Assert.Equal("1700000000.000001", payload.RootElement.GetProperty("thread_ts").GetString());
            Assert.DoesNotContain(TokenPrefix, message, StringComparison.Ordinal);
            Assert.DoesNotContain("prompt", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("response", message, StringComparison.OrdinalIgnoreCase);
        }
        using (var runIdReply = JsonDocument.Parse(handlers["orchestrator"].RequestBodies[1]!))
        {
            Assert.Equal($"Run ID: {RunId}", runIdReply.RootElement.GetProperty("text").GetString());
            Assert.Equal("1700000000.000001", runIdReply.RootElement.GetProperty("thread_ts").GetString());
        }
        Assert.Equal(SlackNotificationSendResult.Skipped,
            await dispatcher!.TrySendAsync(RunId, "Orchestrator", "completed", "needs_human_input", "NeedsHumanInput", DateTimeOffset.UtcNow));
        Assert.Equal(0, dispatcher.FailedSends);
        Assert.Equal(2, handlers["orchestrator"].Requests.Count);
    }

    [Fact]
    public async Task TrySend_ThreadsAgentCompletionAndFinalResultAndUsesSafeFallback()
    {
        using var files = new LifecycleSettingsFiles();
        var handlers = new Dictionary<string, RecordingHandlerBase>(StringComparer.Ordinal);
        using var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(files.ConfigurationPath, files.SecretsPath,
            botId => handlers[botId] = new SuccessRecordingHandler(_ => SuccessResponse(botId)));
        Assert.NotNull(dispatcher);

        Assert.Equal(SlackNotificationSendResult.Sent, await dispatcher!.TrySendAsync(RunId, "Orchestrator", "started", "running", "Prepared", DateTimeOffset.UtcNow));
        Assert.Equal(SlackNotificationSendResult.Sent, await dispatcher.TrySendAsync(RunId, "Implementer", "completed", "succeeded", "Reviewing",
            DateTimeOffset.UtcNow, "agent-one", "Builder", "implementer", "Done", "担当: 実装\n結果: 完了\n未完了・人間判断: なし"));
        Assert.Equal(SlackNotificationSendResult.Sent, await dispatcher.TrySendAsync(RunId, "Reviewer", "completed", "succeeded", "ReadyForHumanReview",
            DateTimeOffset.UtcNow, "agent-two", "Reviewer", "reviewer", "Done", "xoxb-1234567890-secret"));
        Assert.Equal(SlackNotificationSendResult.Sent, await dispatcher.TrySendAsync(RunId, "Orchestrator", "final_result", "succeeded", "ReadyForHumanReview",
            DateTimeOffset.UtcNow, null, null, null, "実装とレビューが完了しました。変更内容を確認してください。", null));

        using var completion = JsonDocument.Parse(handlers["implementer"].RequestBodies.Single()!);
        Assert.Equal("C_orchestrator", completion.RootElement.GetProperty("channel").GetString());
        Assert.Equal("1700000000.000001", completion.RootElement.GetProperty("thread_ts").GetString());
        Assert.Contains("担当: 実装", completion.RootElement.GetProperty("text").GetString(), StringComparison.Ordinal);
        using var finalResult = JsonDocument.Parse(handlers["orchestrator"].RequestBodies.Last()!);
        Assert.False(finalResult.RootElement.TryGetProperty("thread_ts", out _));
        var finalText = finalResult.RootElement.GetProperty("text").GetString()!;
        Assert.StartsWith("*最終結果: 作業が完了しました*\n", finalText, StringComparison.Ordinal);
        Assert.Contains("実装とレビューが完了しました。", finalText, StringComparison.Ordinal);
        Assert.DoesNotContain(RunId, finalText, StringComparison.Ordinal);
        Assert.DoesNotContain("run=", finalText, StringComparison.OrdinalIgnoreCase);

        using var fallback = JsonDocument.Parse(handlers["reviewer"].RequestBodies.Single()!);
        Assert.Contains("安全な完了報告を利用できないため", fallback.RootElement.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-", fallback.RootElement.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrySend_FinalResultUsesHumanSummaryAndRemovesInternalExecutionLines()
    {
        using var files = new LifecycleSettingsFiles();
        var handlers = new Dictionary<string, RecordingHandlerBase>(StringComparer.Ordinal);
        using var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(files.ConfigurationPath, files.SecretsPath,
            botId => handlers[botId] = new SuccessRecordingHandler(_ => SuccessResponse(botId)));
        Assert.NotNull(dispatcher);

        Assert.Equal(SlackNotificationSendResult.Sent, await dispatcher!.TrySendAsync(RunId, "Orchestrator", "final_result",
            "needs_human_input", "NeedsHumanInput", DateTimeOffset.UtcNow, null, null, null, summary:
            $"人の判断が必要な理由: ReviewerはAgent IDの扱いと配送監査に問題があると報告しました。\n指摘内容を確認し、修正を依頼するか判断してください。\nRun ID: {RunId}\nrun={RunId} role=Orchestrator event=final_result status=needs_human_input state=NeedsHumanInput at=2026-11-20T18:51:34.0000000+00:00\n2026-11-20T18:51:34.0000000+00:00", slackCompletionMessage: null));

        using var payload = JsonDocument.Parse(handlers["orchestrator"].RequestBodies.Single()!);
        var message = payload.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("人の判断が必要な理由:", message, StringComparison.Ordinal);
        Assert.Contains("Agent IDの扱いと配送監査に問題があると報告しました。", message, StringComparison.Ordinal);
        Assert.Contains("修正を依頼するか判断してください。", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Run ID", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RunId, message, StringComparison.Ordinal);
        Assert.DoesNotContain("run=", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role=", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("event=", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("status=", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("state=", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("at=", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2026-11-20T18:51:34", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrySend_NeutralizesAgentGeneratedUserMentionMarkup()
    {
        using var files = new LifecycleSettingsFiles();
        var handlers = new Dictionary<string, RecordingHandlerBase>(StringComparer.Ordinal);
        using var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(files.ConfigurationPath, files.SecretsPath,
            botId => handlers[botId] = new SuccessRecordingHandler(_ => SuccessResponse(botId)));
        Assert.NotNull(dispatcher);

        Assert.Equal(SlackNotificationSendResult.Sent, await dispatcher!.TrySendAsync(RunId, "Reviewer", "completed", "succeeded", "ReadyForHumanReview",
            DateTimeOffset.UtcNow, "agent-two", "Reviewer", "reviewer", "Done",
            "担当: Reviewer (Reviewer)\n結果: 完了\n変更・所見: 確認\n確認: 完了\n未完了・人間判断: なし <@U12345678>"));

        using var payload = JsonDocument.Parse(handlers["reviewer"].RequestBodies.Single()!);
        var message = payload.RootElement.GetProperty("text").GetString()!;
        Assert.DoesNotContain("<@U12345678>", message, StringComparison.Ordinal);
        Assert.Contains("‹@U12345678>", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunStart_UsesHumanParentThenThreadedRunIdReply_AndAuditsReplyFailure()
    {
        using var files = new LifecycleSettingsFiles();
        var handlers = new Dictionary<string, RecordingHandlerBase>(StringComparer.Ordinal);
        var orchestratorRequestCount = 0;
        using var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(files.ConfigurationPath, files.SecretsPath,
            botId => handlers[botId] = new SuccessRecordingHandler(_ =>
            {
                if (botId == "orchestrator" && ++orchestratorRequestCount == 2)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("not available") };
                return SuccessResponse(botId);
            }));

        Assert.NotNull(dispatcher);
        var outcome = await dispatcher!.TrySendWithAuditAsync(RunId, "Orchestrator", "started", "running", "Prepared",
            DateTimeOffset.UtcNow, summary: "対象: Slack通知を改善します。\nImplementerが作業を開始します。");

        Assert.Equal(SlackNotificationSendResult.Failed, outcome.Result);
        Assert.Equal("run_id_reply", outcome.FailureStage);
        Assert.Equal("1700000000.000001", outcome.ParentMessageTimestamp);
        Assert.Null(outcome.RunIdReplyTimestamp);
        using var parent = JsonDocument.Parse(handlers["orchestrator"].RequestBodies[0]!);
        using var idReply = JsonDocument.Parse(handlers["orchestrator"].RequestBodies[1]!);
        var parentText = parent.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("*新しい作業を開始しました*", parentText, StringComparison.Ordinal);
        Assert.Contains("対象: Slack通知を改善します。", parentText, StringComparison.Ordinal);
        Assert.DoesNotContain(RunId, parentText, StringComparison.Ordinal);
        Assert.Equal($"Run ID: {RunId}", idReply.RootElement.GetProperty("text").GetString());
        Assert.Equal("1700000000.000001", idReply.RootElement.GetProperty("thread_ts").GetString());

        Assert.Equal(SlackNotificationSendResult.Sent,
            await dispatcher.TrySendAsync(RunId, "Implementer", "started", "running", "Implementing", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TryCreate_TestModeAllowsOneBotIdToServeMultipleRoles()
    {
        using var files = new LifecycleSettingsFiles();
        var routes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Orchestrator"] = "orchestrator",
            ["Implementer"] = "implementer",
            ["Reviewer"] = "implementer"
        };

        using var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(
            files.ConfigurationPath, files.SecretsPath, useTestChannel: true, routes, out var warning);

        Assert.NotNull(dispatcher);
        Assert.Null(warning);
    }

    [Fact]
    public async Task TryCreate_TestModeRoutesEveryRoleToDedicatedTestChannel()
    {
        using var files = new LifecycleSettingsFiles();
        var handlers = new Dictionary<string, RecordingHandlerBase>(StringComparer.Ordinal);
        using var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(files.ConfigurationPath, files.SecretsPath, useTestChannel: true,
            botId => handlers[botId] = new SuccessRecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"ok\":true,\"channel\":\"C_TEST\",\"ts\":\"1700000000.000001\"}}")
            }));

        Assert.NotNull(dispatcher);
        foreach (var (role, botId) in new[]
        {
            ("Orchestrator", "orchestrator"), ("Implementer", "implementer"), ("Reviewer", "reviewer")
        })
        {
            Assert.Equal(SlackNotificationSendResult.Sent, await dispatcher!.TrySendAsync(RunId, role, "started", "running", "Implementing", DateTimeOffset.UtcNow));
            using var payload = JsonDocument.Parse(handlers[botId].RequestBodies[0]!);
            Assert.Equal("C_TEST", payload.RootElement.GetProperty("channel").GetString());
            Assert.Equal($"Bearer {TokenPrefix}{botId}", handlers[botId].Requests[0].Headers.Authorization!.ToString());
            Assert.Equal(role == "Orchestrator" ? 2 : 1, handlers[botId].Requests.Count);
            Assert.Contains("[Ateliers AI TEST] ", payload.RootElement.GetProperty("text").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task VerifyBotIdentitiesAsync_RejectsOneTokenReusedForAllConfiguredRoles()
    {
        using var files = new LifecycleSettingsFiles();
        File.WriteAllText(files.SecretsPath, JsonSerializer.Serialize(new
        {
            tokens = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["orchestrator"] = $"{TokenPrefix}orchestrator",
                ["implementer"] = $"{TokenPrefix}orchestrator",
                ["reviewer"] = $"{TokenPrefix}orchestrator"
            }
        }));
        var handlers = new Dictionary<string, RecordingHandlerBase>(StringComparer.Ordinal);
        using var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(files.ConfigurationPath, files.SecretsPath,
            botId => handlers[botId] = new SuccessRecordingHandler(request =>
            {
                if (request.RequestUri?.AbsolutePath.EndsWith("/auth.test", StringComparison.Ordinal) == true)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"ok\":true,\"user_id\":\"U_orchestrator\",\"bot_id\":\"B_orchestrator\"}")
                    };
                return SuccessResponse(botId);
            }));

        Assert.NotNull(dispatcher);
        Assert.False(await dispatcher!.VerifyBotIdentitiesAsync());
        Assert.All(handlers.Values, handler => Assert.Single(handler.Requests));
        Assert.All(handlers.Values, handler => Assert.EndsWith("/auth.test", handler.Requests.Single().RequestUri!.AbsolutePath, StringComparison.Ordinal));
    }

    [Fact]
    public void TryCreate_TestModeRejectsATestChannelThatMatchesAProductionChannel()
    {
        using var files = new LifecycleSettingsFiles();
        File.WriteAllText(files.ConfigurationPath, """{"testChannelId":"C_orchestrator","bots":[{"id":"orchestrator","name":"orchestrator bot","botUserId":"U_orchestrator","channelId":"C_orchestrator","enabled":true},{"id":"implementer","name":"implementer bot","botUserId":"U_implementer","channelId":"C_implementer","enabled":true},{"id":"reviewer","name":"reviewer bot","botUserId":"U_reviewer","channelId":"C_reviewer","enabled":true}]}""");

        var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(files.ConfigurationPath, files.SecretsPath, useTestChannel: true, out var warning);

        Assert.Null(dispatcher);
        Assert.Contains("Bot", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_TestModeRejectsDuplicateBotIdentity()
    {
        using var files = new LifecycleSettingsFiles();
        File.WriteAllText(files.ConfigurationPath, """{"testChannelId":"C_TEST","bots":[{"id":"orchestrator","name":"orchestrator bot","botUserId":"U_shared","channelId":"C_orchestrator","enabled":true},{"id":"implementer","name":"implementer bot","botUserId":"U_shared","channelId":"C_implementer","enabled":true},{"id":"reviewer","name":"reviewer bot","botUserId":"U_reviewer","channelId":"C_reviewer","enabled":true}]}""");

        var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(files.ConfigurationPath, files.SecretsPath, useTestChannel: true, out var warning);

        Assert.Null(dispatcher);
        Assert.Contains("Bot", warning, StringComparison.Ordinal);
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
        Content = new StringContent("{\"ok\":true,\"channel\":\"C_orchestrator\",\"ts\":\"1700000000.000001\"}")
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
            File.WriteAllText(ConfigurationPath, JsonSerializer.Serialize(new { testChannelId = "C_TEST", bots }));
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
