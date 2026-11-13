using System.Globalization;

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
    private static readonly (string Role, string BotId)[] BotRoutes =
    [
        ("Orchestrator", "orchestrator"),
        ("Implementer", "implementer"),
        ("Reviewer", "reviewer")
    ];

    private static readonly HashSet<string> EventTypes = new(StringComparer.Ordinal)
    {
        "started", "completed", "failed", "needs_human_input"
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
    private int failedSends;

    private SlackLifecycleNotificationDispatcher(Dictionary<string, SlackBotClient> clients) => this.clients = clients;

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
        return TryCreate(configurationPath, secretsPath, useTestChannel: false, handlerFactory: null, requestTimeout: null, out warning);
    }

    public static SlackLifecycleNotificationDispatcher? TryCreate(string configurationPath, string secretsPath, bool useTestChannel, out string? warning)
    {
        return TryCreate(configurationPath, secretsPath, useTestChannel, handlerFactory: null, requestTimeout: null, out warning);
    }

    internal static SlackLifecycleNotificationDispatcher? TryCreate(
        string configurationPath,
        string secretsPath,
        Func<string, HttpMessageHandler>? handlerFactory,
        TimeSpan? requestTimeout = null) =>
        TryCreate(configurationPath, secretsPath, useTestChannel: false, handlerFactory, requestTimeout, out _);

    internal static SlackLifecycleNotificationDispatcher? TryCreate(
        string configurationPath,
        string secretsPath,
        bool useTestChannel,
        Func<string, HttpMessageHandler>? handlerFactory,
        TimeSpan? requestTimeout = null) =>
        TryCreate(configurationPath, secretsPath, useTestChannel, handlerFactory, requestTimeout, out _);

    private static SlackLifecycleNotificationDispatcher? TryCreate(
        string configurationPath,
        string secretsPath,
        bool useTestChannel,
        Func<string, HttpMessageHandler>? handlerFactory,
        TimeSpan? requestTimeout,
        out string? warning)
    {
        warning = null;
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
            var botUserIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (role, botId) in BotRoutes)
            {
                var configuration = SlackBotConfiguration.Load(configurationPath, secretsPath, botId, useTestChannel);
                if (useTestChannel && string.Equals(configuration.ChannelId, configuration.Bot.ChannelId, StringComparison.Ordinal))
                    throw new InvalidOperationException();
                if (useTestChannel && !botUserIds.Add(configuration.Bot.BotUserId))
                    throw new InvalidOperationException();
                clients.Add(role, handlerFactory is null
                    ? new SlackBotClient(configuration)
                    : new SlackBotClient(configuration, handlerFactory(botId), requestTimeout));
            }
            return new SlackLifecycleNotificationDispatcher(clients);
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
        foreach (var (role, _) in BotRoutes)
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
        DateTimeOffset timestampUtc, CancellationToken cancellationToken = default)
    {
        if (!clients.TryGetValue(role, out var client) ||
            !Guid.TryParseExact(runId, "N", out _) ||
            !EventTypes.Contains(eventType) || !Statuses.Contains(status) || !States.Contains(state))
        {
            Interlocked.Increment(ref failedSends);
            return SlackNotificationSendResult.Failed;
        }

        // NeedsHumanInput専用イベントの直後に同じ状態の終端イベントも出るため、重複を抑止します。
        if (eventType == "completed" && status == "needs_human_input") return SlackNotificationSendResult.Skipped;

        var message = string.Create(CultureInfo.InvariantCulture,
            $"[Ateliers AI] run={runId} role={role} event={eventType} status={status} state={state} at={timestampUtc.ToUniversalTime():O}");
        try
        {
            await client.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
            return SlackNotificationSendResult.Sent;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref failedSends);
            return SlackNotificationSendResult.Failed;
        }
    }

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
