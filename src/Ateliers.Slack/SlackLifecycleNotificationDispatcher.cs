using System.Text.RegularExpressions;

namespace Ateliers.Slack;

public enum SlackNotificationSendResult
{
    Sent,
    Skipped,
    Failed,
    Dropped,
    Abandoned
}

public sealed record SlackNotificationDeliveryOutcome(SlackNotificationSendResult Result,
    string? MessageTimestamp = null, string? ParentMessageTimestamp = null,
    string? RunIdReplyTimestamp = null, string? FailureStage = null);

/// <summary>ライフサイクルイベントを役割別Botから短い固定形式で通知します。</summary>
public sealed class SlackLifecycleNotificationDispatcher : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> DefaultBotRoutes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Orchestrator"] = "orchestrator", ["Implementer"] = "implementer", ["Reviewer"] = "reviewer"
    };

    private static readonly HashSet<string> EventTypes = new(StringComparer.Ordinal)
    {
        "started", "completed", "failed", "needs_human_input", "final_result"
    };

    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal)
    {
        "running", "succeeded", "failed", "needs_human_input"
    };

    private static readonly HashSet<string> States = new(StringComparer.Ordinal)
    {
        "Prepared", "Implementing", "Reviewing", "Revising", "ReadyForHumanReview", "NeedsHumanInput", "Failed"
    };

    private readonly Dictionary<string, SlackBotClient> clients;
    private readonly IReadOnlyDictionary<string, string> botRoutes;
    private readonly bool useTestChannel;
    private readonly Dictionary<string, string> runThreads = new(StringComparer.Ordinal);
    private readonly object threadLock = new();
    private int failedSends;

    private SlackLifecycleNotificationDispatcher(Dictionary<string, SlackBotClient> clients,
        IReadOnlyDictionary<string, string> botRoutes, bool useTestChannel)
    {
        this.clients = clients;
        this.botRoutes = botRoutes;
        this.useTestChannel = useTestChannel;
    }

    /// <summary>ローカル設定からDispatcherを作ります。設定が未配置または有効なBotがない場合はnullを返します。</summary>
    public static SlackLifecycleNotificationDispatcher? TryCreate(string configurationPath, string secretsPath) =>
        TryCreate(configurationPath, secretsPath, useTestChannel: false);

    public static SlackLifecycleNotificationDispatcher? TryCreate(string configurationPath, string secretsPath, bool useTestChannel)
    {
        return TryCreate(configurationPath, secretsPath, useTestChannel, out _);
    }

    /// <summary>不完全または不正な設定時はnullを返し、秘密情報を含まない警告理由を返します。</summary>
    public static SlackLifecycleNotificationDispatcher? TryCreate(string configurationPath, string secretsPath, out string? warning)
    {
        return TryCreate(configurationPath, secretsPath, useTestChannel: false, handlerFactory: null, requestTimeout: null, DefaultBotRoutes, out warning);
    }

    public static SlackLifecycleNotificationDispatcher? TryCreate(string configurationPath, string secretsPath, bool useTestChannel, out string? warning)
    {
        return TryCreate(configurationPath, secretsPath, useTestChannel, handlerFactory: null, requestTimeout: null, DefaultBotRoutes, out warning);
    }

    public static SlackLifecycleNotificationDispatcher? TryCreate(string configurationPath, string secretsPath, bool useTestChannel,
        IReadOnlyDictionary<string, string> botRoutes, out string? warning) =>
        TryCreate(configurationPath, secretsPath, useTestChannel, handlerFactory: null, requestTimeout: null, botRoutes, out warning);

    internal static SlackLifecycleNotificationDispatcher? TryCreate(
        string configurationPath,
        string secretsPath,
        Func<string, HttpMessageHandler>? handlerFactory,
        TimeSpan? requestTimeout = null) =>
        TryCreate(configurationPath, secretsPath, useTestChannel: false, handlerFactory, requestTimeout, DefaultBotRoutes, out _);

    internal static SlackLifecycleNotificationDispatcher? TryCreate(
        string configurationPath,
        string secretsPath,
        bool useTestChannel,
        Func<string, HttpMessageHandler>? handlerFactory,
        TimeSpan? requestTimeout = null) =>
        TryCreate(configurationPath, secretsPath, useTestChannel, handlerFactory, requestTimeout, DefaultBotRoutes, out _);

    private static SlackLifecycleNotificationDispatcher? TryCreate(
        string configurationPath,
        string secretsPath,
        bool useTestChannel,
        Func<string, HttpMessageHandler>? handlerFactory,
        TimeSpan? requestTimeout,
        IReadOnlyDictionary<string, string> botRoutes,
        out string? warning)
    {
        warning = null;
        if (botRoutes is null || !botRoutes.ContainsKey("Orchestrator") || !botRoutes.ContainsKey("Implementer") || !botRoutes.ContainsKey("Reviewer") ||
            botRoutes.Values.Any(string.IsNullOrWhiteSpace))
        {
            warning = "Slack通知のBotルート設定が不正です。";
            return null;
        }
        if (string.IsNullOrWhiteSpace(configurationPath) || !Path.IsPathFullyQualified(configurationPath))
        {
            warning = "Slack通知の設定が不正です。";
            return null;
        }
        if (!File.Exists(configurationPath)) return null;
        if (string.IsNullOrWhiteSpace(secretsPath) || !Path.IsPathFullyQualified(secretsPath) || !File.Exists(secretsPath))
        {
            warning = "Slack通知のsecrets設定が不足または不正です。";
            return null;
        }

        var clients = new Dictionary<string, SlackBotClient>(StringComparer.Ordinal);
        try
        {
            var botUserIds = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (role, botId) in botRoutes)
            {
                var configuration = SlackBotConfiguration.Load(configurationPath, secretsPath, botId, useTestChannel);
                if (useTestChannel && string.Equals(configuration.ChannelId, configuration.Bot.ChannelId, StringComparison.Ordinal))
                    throw new InvalidOperationException();
                if (useTestChannel && botUserIds.TryGetValue(configuration.Bot.BotUserId, out var existingBotId) &&
                    !string.Equals(existingBotId, botId, StringComparison.Ordinal))
                    throw new InvalidOperationException();
                if (useTestChannel) botUserIds[configuration.Bot.BotUserId] = botId;
                clients.Add(role, handlerFactory is null
                    ? new SlackBotClient(configuration)
                    : new SlackBotClient(configuration, handlerFactory(botId), requestTimeout));
            }
            return new SlackLifecycleNotificationDispatcher(clients, botRoutes.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal), useTestChannel);
        }
        catch (Exception)
        {
            DisposeClients(clients.Values);
            warning = "Slack通知のBot定義またはsecrets設定が不足または不正です。";
            return null;
        }
    }

    public int FailedSends => Volatile.Read(ref failedSends);

    /// <summary>全役割のTokenをSlack auth.testで照合し、設定Bot User IDとの一致だけを返します。</summary>
    public async Task<bool> VerifyBotIdentitiesAsync(CancellationToken cancellationToken = default)
    {
        var allMatched = true;
        foreach (var (role, _) in botRoutes)
        {
            try
            {
                if (!await clients[role].VerifyBotIdentityAsync(cancellationToken).ConfigureAwait(false)) allMatched = false;
            }
            catch (SlackNotificationException)
            {
                allMatched = false;
            }
        }
        return allMatched;
    }

    /// <summary>送信結果を返し、Slack障害は例外として呼出し元へ伝えません。</summary>
    public async Task<SlackNotificationSendResult> TrySendAsync(string runId, string role, string eventType, string status, string state,
        DateTimeOffset timestampUtc, CancellationToken cancellationToken = default) =>
        await TrySendAsync(runId, role, eventType, status, state, timestampUtc, null, null, null, null, null, cancellationToken).ConfigureAwait(false);

    public async Task<SlackNotificationSendResult> TrySendAsync(string runId, string role, string eventType, string status, string state,
        DateTimeOffset timestampUtc, string? agentId, string? agentName, string? botId, string? summary, string? slackCompletionMessage,
        CancellationToken cancellationToken = default) =>
        (await TrySendWithAuditAsync(runId, role, eventType, status, state, timestampUtc, agentId, agentName, botId,
            summary, slackCompletionMessage, cancellationToken).ConfigureAwait(false)).Result;

    public async Task<SlackNotificationDeliveryOutcome> TrySendWithAuditAsync(string runId, string role, string eventType, string status, string state,
        DateTimeOffset timestampUtc, string? agentId = null, string? agentName = null, string? botId = null,
        string? summary = null, string? slackCompletionMessage = null, CancellationToken cancellationToken = default)
    {
        if (!clients.TryGetValue(role, out var client) || !botRoutes.TryGetValue(role, out var expectedBotId) ||
            !Guid.TryParseExact(runId, "N", out _) ||
            !EventTypes.Contains(eventType) || !Statuses.Contains(status) || !States.Contains(state) ||
            !IsValidEventStatus(role, eventType, status) ||
            (botId is not null && !string.Equals(botId, expectedBotId, StringComparison.Ordinal)))
        {
            Interlocked.Increment(ref failedSends);
            return new(SlackNotificationSendResult.Failed, FailureStage: "validation");
        }

        // NeedsHumanInput専用イベントの直後に同じ状態の終端イベントも出るため、重複を抑止します。
        if (eventType == "completed" && status == "needs_human_input") return new(SlackNotificationSendResult.Skipped);

        var label = useTestChannel ? "[Ateliers AI TEST]" : "[Ateliers AI]";
        var message = FormatMessage(label, runId, role, eventType, status, agentId, agentName, summary, slackCompletionMessage);
        string? threadTs = null;
        var parent = role == "Orchestrator" && eventType == "started";
        var finalResult = eventType == "final_result";
        var threadAvailable = true;
        if (!parent && !finalResult)
        {
            lock (threadLock)
            {
                threadAvailable = runThreads.TryGetValue(runId, out threadTs);
            }
        }
        if (!threadAvailable) message = $"Runのスレッドを利用できなかったため、チャンネルへ投稿します。\n{message}";
        var channelId = clients["Orchestrator"].ChannelId;
        if (parent)
        {
            SlackPostMessageResult parentMessage;
            try
            {
                parentMessage = await client.SendMessageAsync(message, channelId, threadTs: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref failedSends);
                return new(SlackNotificationSendResult.Failed, FailureStage: "parent_message");
            }

            lock (threadLock) runThreads[runId] = parentMessage.Timestamp;
            try
            {
                var runIdReply = await client.SendMessageAsync($"Run ID: {runId}", channelId, parentMessage.Timestamp, cancellationToken).ConfigureAwait(false);
                return new(SlackNotificationSendResult.Sent, ParentMessageTimestamp: parentMessage.Timestamp,
                    RunIdReplyTimestamp: runIdReply.Timestamp);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref failedSends);
                return new(SlackNotificationSendResult.Failed, ParentMessageTimestamp: parentMessage.Timestamp,
                    FailureStage: "run_id_reply");
            }
        }

        try
        {
            var sent = await client.SendMessageAsync(message, channelId, threadTs, cancellationToken).ConfigureAwait(false);
            if (finalResult)
            {
                lock (threadLock) runThreads.Remove(runId);
            }
            return new(SlackNotificationSendResult.Sent, MessageTimestamp: sent.Timestamp);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref failedSends);
            return new(SlackNotificationSendResult.Failed, FailureStage: threadAvailable ? "message" : "thread_fallback_message");
        }
    }

    private static string FormatMessage(string label, string runId, string role, string eventType, string status,
        string? agentId, string? agentName, string? summary, string? slackCompletionMessage)
    {
        var headline = eventType switch
        {
            "final_result" => status switch
            {
                "succeeded" => "最終結果: 作業が完了しました",
                "failed" => "最終結果: 作業を完了できませんでした",
                _ => "最終結果: 人による確認が必要です"
            },
            "needs_human_input" => "人による確認が必要です",
            "failed" => role == "Reviewer" ? "レビューを完了できませんでした" : "作業を完了できませんでした",
            "completed" when role == "Orchestrator" => status == "succeeded" ? "作業とレビューが完了しました" : "人による確認が必要です",
            "completed" => "作業完了の報告",
            "started" when role == "Orchestrator" => "新しい作業を開始しました",
            "started" => $"{SanitizeNotificationText(agentName ?? agentId ?? role, runId, 3_000) ?? "担当Agent"} が作業を開始しました",
            _ => "作業状況を更新しました"
        };

        var maximumBodyLength = status == "needs_human_input" ? 12_000 : 3_000;
        string? body = eventType == "completed" && role != "Orchestrator"
            ? SanitizeNotificationText(slackCompletionMessage, runId, 3_000)
            : SanitizeNotificationText(summary, runId, maximumBodyLength);
        if (body is null)
        {
            body = eventType switch
            {
                "started" when role == "Orchestrator" => "タスクの目的を確認し、担当Agentの作業を開始します。",
                "started" when role == "Reviewer" => "成果物の確認を始めます。指摘が見つかった場合は修正を依頼し、確認後に結果を報告します。",
                "started" => "担当作業を進めます。完了後、Reviewerによる確認へ進みます。",
                "completed" when role != "Orchestrator" => "作業は完了しました。安全な完了報告を利用できないため、実行記録で作業内容と確認結果を確認してください。",
                "failed" => "実行記録を確認し、原因と次の対応を判断してください。",
                "needs_human_input" => "実行記録を確認し、次の対応を判断してください。",
                "final_result" when status == "succeeded" => "変更内容とレビュー結果を確認してください。",
                "final_result" => "実行記録を確認し、原因と次の対応を判断してください。",
                _ => "実行記録で詳細を確認してください。"
            };
        }

        var prefix = useTestLabel(label) ? $"{label} " : string.Empty;
        return $"{prefix}*{headline}*\n{body}";
    }

    private static bool useTestLabel(string label) => label == "[Ateliers AI TEST]";

    private static string? SanitizeNotificationText(string? message, string runId, int maximumLength)
    {
        var sanitized = SanitizeAgentMessage(message, maximumLength);
        if (sanitized is null) return null;
        sanitized = Regex.Replace(sanitized, @"(?im)^.*\b(?:run|role|event|status|state|at)=\S+.*(?:\r?\n|$)", string.Empty,
            RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(sanitized, @"(?im)^\s*\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?\s*$", string.Empty,
            RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(sanitized, @"(?im)^.*\brun[\s_-]*id\s*[:=].*(?:\r?\n|$)", string.Empty, RegexOptions.CultureInvariant);
        sanitized = sanitized.Replace(runId, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (sanitized.Length > maximumLength)
        {
            const string omitted = "\n\n（通知の文字数上限に達しました。続きは実行記録を確認してください。）";
            sanitized = sanitized[..(maximumLength - omitted.Length)].TrimEnd() + omitted;
        }
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }

    private static string? SanitizeAgentMessage(string? message, int maximumLength = 3_000)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > maximumLength * 4 ||
            message.Contains("-----BEGIN", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(message, @"(?i)\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9-]{10,})\b") ||
            Regex.IsMatch(message, @"(?i)\bBearer\s+\S+") ||
            Regex.IsMatch(message, @"(?i)(?:api[_-]?key|access[_-]?token|client[_-]?secret|password|authorization)\s*[:=]\s*\S+") ||
            Regex.IsMatch(message, @"(?i)\bhttps?://[^/@:\s]+:[^/@\s]+@")) return null;
        return Regex.Replace(message, @"(?i)@(channel|here|everyone)", "＠$1", RegexOptions.CultureInvariant)
            .Replace("<!", "‹!", StringComparison.Ordinal)
            .Replace("<@", "‹@", StringComparison.Ordinal).Trim();
    }

    private static bool IsValidEventStatus(string role, string eventType, string status) => eventType switch
    {
        "started" => status == "running",
        "completed" => role == "Orchestrator" ? status is "succeeded" or "needs_human_input" : status == "succeeded",
        "failed" => status == "failed",
        "needs_human_input" => role == "Orchestrator" && status == "needs_human_input",
        "final_result" => role == "Orchestrator" && status is "succeeded" or "failed" or "needs_human_input",
        _ => false
    };

    public void Dispose()
    {
        DisposeClients(clients.Values);
    }

    private static void DisposeClients(IEnumerable<SlackBotClient> clients)
    {
        foreach (var client in clients)
        {
            try { client.Dispose(); }
            catch (Exception) { }
        }
    }
}
