using System.Globalization;
using System.Text.RegularExpressions;

namespace Ateliers.Slack;

public enum SlackNotificationSendResult
{
    Sent,
    Skipped,
    Failed
}

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
        CancellationToken cancellationToken = default)
    {
        if (!clients.TryGetValue(role, out var client) || !botRoutes.TryGetValue(role, out var expectedBotId) ||
            !Guid.TryParseExact(runId, "N", out _) ||
            !EventTypes.Contains(eventType) || !Statuses.Contains(status) || !States.Contains(state) ||
            !IsValidEventStatus(role, eventType, status) ||
            (botId is not null && !string.Equals(botId, expectedBotId, StringComparison.Ordinal)))
        {
            Interlocked.Increment(ref failedSends);
            return SlackNotificationSendResult.Failed;
        }

        // NeedsHumanInput専用イベントの直後に同じ状態の終端イベントも出るため、重複を抑止します。
        if (eventType == "completed" && status == "needs_human_input") return SlackNotificationSendResult.Skipped;

        var label = useTestChannel ? "[Ateliers AI TEST]" : "[Ateliers AI]";
        var message = FormatMessage(label, runId, role, eventType, status, state, timestampUtc, agentId, agentName, summary, slackCompletionMessage);
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
        if (!threadAvailable) message = $"スレッド親投稿を利用できないため、チャンネルへ単独投稿します。\n{message}";
        var channelId = clients["Orchestrator"].ChannelId;
        try
        {
            var sent = await client.SendMessageAsync(message, channelId, threadTs, cancellationToken).ConfigureAwait(false);
            if (parent)
            {
                lock (threadLock) runThreads[runId] = sent.Timestamp;
            }
            if (finalResult)
            {
                lock (threadLock) runThreads.Remove(runId);
            }
            return SlackNotificationSendResult.Sent;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref failedSends);
            return SlackNotificationSendResult.Failed;
        }
    }

    private static string FormatMessage(string label, string runId, string role, string eventType, string status, string state,
        DateTimeOffset timestampUtc, string? agentId, string? agentName, string? summary, string? slackCompletionMessage)
    {
        string text;
        if (eventType == "started" && role == "Orchestrator") text = $"Run開始: {runId}";
        else if (eventType == "final_result") text = SanitizeAgentMessage(summary) ?? "最終結果を記録しました。詳細はOrchestratorの実行記録を確認してください。";
        else if (eventType == "started") text = $"作業開始: {SanitizeAgentMessage(agentName) ?? agentId ?? role} ({role})";
        else if (eventType == "completed" && role != "Orchestrator")
            text = SanitizeAgentMessage(slackCompletionMessage) ?? $"{agentId ?? role} ({role}) の作業が完了しました。詳細は実行記録を確認してください。";
        else if (eventType == "failed") text = $"{role} の作業が失敗しました。状態: {state}。詳細は実行記録を確認してください。";
        else if (eventType == "needs_human_input") text = "人間の確認が必要です。詳細は実行記録を確認してください。";
        else text = role == "Orchestrator" && status == "succeeded" ? "Runが完了しました。" : $"Runが終了しました。状態: {state}。";

        var metadata = string.Create(CultureInfo.InvariantCulture,
            $"{label} run={runId} role={role} event={eventType} status={status} state={state} at={timestampUtc.ToUniversalTime():O}");
        return $"{text}\n{metadata}";
    }

    private static string? SanitizeAgentMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 3_000 ||
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
