using Ateliers.Ai.Orchestrator.Core;
using Ateliers.Slack;
using System.Threading.Channels;

namespace Ateliers.Ai.Orchestrator.Cli;

/// <summary>JSONL保存とSlack通知を独立して実行するライフサイクルWriterです。</summary>
internal sealed class LifecycleNotificationEventWriter : ILifecycleEventWriter, IDisposable
{
    private static readonly TimeSpan NotificationDrainTimeout = TimeSpan.FromSeconds(2);
    private readonly JsonlLifecycleEventWriter jsonlWriter;
    private readonly SlackLifecycleNotificationDispatcher? slackDispatcher;
    private readonly object notificationStateLock = new();
    private Channel<LifecycleEvent>? slackQueue;
    private CancellationTokenSource? slackCancellation;
    private Task? slackWorker;
    private int droppedNotifications;
    private int failedNotifications;
    private int sentNotifications;
    private int pendingNotifications;
    private int abandonedNotifications;
    private LifecycleEvent? inFlightNotification;
    private bool inFlightMarkedAbandoned;
    private bool notificationAccountingFinalized;

    public LifecycleNotificationEventWriter(string lifecycleLogPath, bool notificationsDisabled)
        : this(lifecycleLogPath, notificationsDisabled, useTestChannel: false, configurationPath: null, secretsPath: null, discoverSettings: true, agentBotRoutes: null)
    {
    }

    public LifecycleNotificationEventWriter(string lifecycleLogPath, bool notificationsDisabled, bool useTestChannel)
        : this(lifecycleLogPath, notificationsDisabled, useTestChannel, configurationPath: null, secretsPath: null, discoverSettings: true, agentBotRoutes: null)
    {
    }

    internal LifecycleNotificationEventWriter(string lifecycleLogPath, bool notificationsDisabled,
        string configurationPath, string? secretsPath)
        : this(lifecycleLogPath, notificationsDisabled, useTestChannel: false, configurationPath, secretsPath, discoverSettings: false, agentBotRoutes: null)
    {
    }

    internal LifecycleNotificationEventWriter(string lifecycleLogPath, bool notificationsDisabled,
        string configurationPath, string? secretsPath, bool useTestChannel)
        : this(lifecycleLogPath, notificationsDisabled, useTestChannel, configurationPath, secretsPath, discoverSettings: false, agentBotRoutes: null)
    {
    }

    internal LifecycleNotificationEventWriter(string lifecycleLogPath, bool notificationsDisabled,
        bool useTestChannel, IReadOnlyDictionary<string, string> agentBotRoutes)
        : this(lifecycleLogPath, notificationsDisabled, useTestChannel, configurationPath: null, secretsPath: null,
            discoverSettings: true, agentBotRoutes)
    {
    }

    internal LifecycleNotificationEventWriter(string lifecycleLogPath, bool notificationsDisabled,
        string configurationPath, string? secretsPath, bool useTestChannel, IReadOnlyDictionary<string, string> agentBotRoutes)
        : this(lifecycleLogPath, notificationsDisabled, useTestChannel, configurationPath, secretsPath, discoverSettings: false, agentBotRoutes)
    {
    }

    internal LifecycleNotificationEventWriter(string lifecycleLogPath, bool notificationsDisabled,
        Func<LifecycleEvent, CancellationToken, Task<SlackNotificationSendResult>> notificationSender)
    {
        ArgumentNullException.ThrowIfNull(notificationSender);
        jsonlWriter = new JsonlLifecycleEventWriter(lifecycleLogPath);
        if (!notificationsDisabled) StartNotificationWorker(async (item, cancellationToken) =>
        {
            var result = await notificationSender(item, cancellationToken).ConfigureAwait(false);
            return new SlackNotificationDeliveryOutcome(result);
        });
    }

    internal LifecycleNotificationEventWriter(string lifecycleLogPath, bool notificationsDisabled,
        Func<LifecycleEvent, CancellationToken, Task<SlackNotificationDeliveryOutcome>> notificationSender)
    {
        ArgumentNullException.ThrowIfNull(notificationSender);
        jsonlWriter = new JsonlLifecycleEventWriter(lifecycleLogPath);
        if (!notificationsDisabled) StartNotificationWorker(notificationSender);
    }

    private LifecycleNotificationEventWriter(string lifecycleLogPath, bool notificationsDisabled,
        bool useTestChannel, string? configurationPath, string? secretsPath, bool discoverSettings,
        IReadOnlyDictionary<string, string>? agentBotRoutes)
    {
        jsonlWriter = new JsonlLifecycleEventWriter(lifecycleLogPath);
        if (notificationsDisabled) return;

        try
        {
            if (discoverSettings && !TryFindSlackSettings(out configurationPath, out secretsPath)) return;
            slackDispatcher = agentBotRoutes is null
                ? SlackLifecycleNotificationDispatcher.TryCreate(configurationPath ?? string.Empty, secretsPath ?? string.Empty, useTestChannel, out var warning)
                : SlackLifecycleNotificationDispatcher.TryCreate(configurationPath ?? string.Empty, secretsPath ?? string.Empty, useTestChannel,
                    agentBotRoutes, out warning);
            if (warning is not null)
                WriteWarning($"警告: {warning}通知をスキップします。実行とJSONL記録は継続します。");
            if (slackDispatcher is not null)
                StartNotificationWorker(async (item, cancellationToken) =>
                {
                    return await slackDispatcher.TrySendWithAuditAsync(item.RunId, item.Role, item.EventType, item.Status, item.State,
                        item.TimestampUtc, item.AgentId, item.AgentName, item.SlackBotId, item.Summary, item.SlackCompletionMessage,
                        cancellationToken).ConfigureAwait(false);
                });
        }
        catch (Exception)
        {
            WriteWarning("警告: Slack通知の設定を読み込めないため、通知をスキップします。実行とJSONL記録は継続します。");
        }
    }

    public int FailedWrites => jsonlWriter.FailedWrites;
    public int FailedSlackSends => Volatile.Read(ref droppedNotifications) + Volatile.Read(ref failedNotifications) +
        Volatile.Read(ref abandonedNotifications);
    public int SentSlackSends => Volatile.Read(ref sentNotifications);

    public bool TryAppend(LifecycleEvent item)
    {
        var recorded = jsonlWriter.TryAppend(item);
        if (slackQueue is { } queue)
        {
            string? deliveryResult = null;
            lock (notificationStateLock)
            {
                if (notificationAccountingFinalized)
                {
                    Interlocked.Increment(ref abandonedNotifications);
                    deliveryResult = "abandoned";
                }
                else
                {
                    Interlocked.Increment(ref pendingNotifications);
                    if (!queue.Writer.TryWrite(item))
                    {
                        Interlocked.Decrement(ref pendingNotifications);
                        Interlocked.Increment(ref droppedNotifications);
                        deliveryResult = "dropped";
                    }
                }
            }
            if (deliveryResult is not null)
                RecordDeliveryResult(item, new SlackNotificationDeliveryOutcome(
                    deliveryResult == "abandoned" ? SlackNotificationSendResult.Abandoned : SlackNotificationSendResult.Dropped,
                    FailureStage: deliveryResult));
        }
        return recorded;
    }

    public async Task FlushSlackNotificationsAsync(CancellationToken cancellationToken = default)
    {
        if (slackQueue is not { } queue || slackWorker is not { } worker) return;
        queue.Writer.TryComplete();
        if (cancellationToken.IsCancellationRequested)
        {
            CancelAndAccountPendingNotifications();
            return;
        }

        try
        {
            await worker.WaitAsync(NotificationDrainTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            CancelAndAccountPendingNotifications();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelAndAccountPendingNotifications();
        }
    }

    public void Dispose()
    {
        slackQueue?.Writer.TryComplete();
        CancelAndAccountPendingNotifications();
        slackDispatcher?.Dispose();
        slackCancellation?.Dispose();
    }

    private void StartNotificationWorker(Func<LifecycleEvent, CancellationToken, Task<SlackNotificationDeliveryOutcome>> notificationSender)
    {
        slackQueue = Channel.CreateBounded<LifecycleEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        slackCancellation = new CancellationTokenSource();
        slackWorker = Task.Run(async () =>
        {
            try
            {
                while (await slackQueue.Reader.WaitToReadAsync(slackCancellation.Token).ConfigureAwait(false))
                {
                    LifecycleEvent item;
                    lock (notificationStateLock)
                    {
                        if (!slackQueue.Reader.TryRead(out var next)) continue;
                        item = next;
                        inFlightNotification = item;
                        inFlightMarkedAbandoned = false;
                    }
                    SlackNotificationDeliveryOutcome outcome;
                    try
                    {
                        outcome = await notificationSender(item, slackCancellation.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        outcome = new SlackNotificationDeliveryOutcome(SlackNotificationSendResult.Failed);
                    }
                    if (CompleteNotification(item, outcome.Result))
                        RecordDeliveryResult(item, outcome);
                    if (slackCancellation.IsCancellationRequested) return;
                }
            }
            catch (OperationCanceledException) when (slackCancellation.IsCancellationRequested)
            {
                // The workflow result is independent from notifications still queued at shutdown.
            }
            catch (Exception)
            {
                // Unexpected worker failures do not affect the workflow.
                CancelAndAccountPendingNotifications();
            }
        });
    }

    private bool CompleteNotification(LifecycleEvent item, SlackNotificationSendResult result)
    {
        lock (notificationStateLock)
        {
            Interlocked.Decrement(ref pendingNotifications);
            var shouldRecordResult = ReferenceEquals(inFlightNotification, item) && !inFlightMarkedAbandoned;
            if (ReferenceEquals(inFlightNotification, item))
            {
                inFlightNotification = null;
                inFlightMarkedAbandoned = false;
            }
            if (shouldRecordResult && result == SlackNotificationSendResult.Failed)
                Interlocked.Increment(ref failedNotifications);
            else if (shouldRecordResult && result == SlackNotificationSendResult.Sent)
                Interlocked.Increment(ref sentNotifications);
            return shouldRecordResult;
        }
    }

    private void RecordDeliveryResult(LifecycleEvent source, SlackNotificationDeliveryOutcome outcome)
    {
        _ = jsonlWriter.TryAppend(new LifecycleEvent
        {
            RunId = source.RunId,
            TimestampUtc = DateTimeOffset.UtcNow,
            Role = source.Role,
            EventType = "slack_delivery_result",
            Status = outcome.Result.ToString().ToLowerInvariant(),
            State = source.State,
            AgentId = source.AgentId,
            AgentName = source.AgentName,
            SlackBotId = source.SlackBotId,
            RelatedId = source.EventType,
            Summary = "Slack通知配送結果です。本文は監査記録へ複製していません。",
            SlackMessageTimestamp = outcome.MessageTimestamp,
            SlackParentMessageTimestamp = outcome.ParentMessageTimestamp,
            SlackRunIdReplyTimestamp = outcome.RunIdReplyTimestamp,
            SlackDeliveryFailureStage = outcome.FailureStage
        });
    }

    private void CancelAndAccountPendingNotifications()
    {
        var notificationsForAbandonment = new List<LifecycleEvent>();
        lock (notificationStateLock)
        {
            if (notificationAccountingFinalized) return;
            notificationAccountingFinalized = true;
            if (slackQueue is { } queue)
            {
                while (queue.Reader.TryRead(out var item))
                {
                    notificationsForAbandonment.Add(item);
                    Interlocked.Decrement(ref pendingNotifications);
                }
            }
            if (inFlightNotification is { } inFlight && !inFlightMarkedAbandoned)
            {
                inFlightMarkedAbandoned = true;
                notificationsForAbandonment.Add(inFlight);
            }
            Interlocked.Add(ref abandonedNotifications, notificationsForAbandonment.Count);
        }
        foreach (var item in notificationsForAbandonment)
            RecordDeliveryResult(item, new SlackNotificationDeliveryOutcome(SlackNotificationSendResult.Abandoned, FailureStage: "abandoned"));
        try { slackCancellation?.Cancel(); }
        catch (Exception) { }
    }

    private static void WriteWarning(string message)
    {
        try { Console.Error.WriteLine(message); }
        catch (Exception) { }
    }

    private static bool TryFindSlackSettings(out string configurationPath, out string secretsPath)
    {
        configurationPath = string.Empty;
        secretsPath = string.Empty;
        if (SlackConfigurationLocator.TryFind(out configurationPath, out secretsPath)) return true;
        configurationPath = string.Empty;
        secretsPath = string.Empty;
        return false;
    }
}

internal static class SlackConfigurationLocator
{
    internal static bool TryFind(out string configurationPath, out string secretsPath,
        string? localAppData = null, string? applicationBaseDirectory = null)
    {
        configurationPath = string.Empty;
        secretsPath = string.Empty;
        localAppData ??= Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var userConfigurationDirectory = Path.Combine(localAppData, "Ateliers-Dev", "Ai-Orchestrator", "config");
            if (TryFindInDirectory(userConfigurationDirectory, out configurationPath, out secretsPath)) return true;
        }

        // Development checkouts keep their local config beside the CLI project. Resolve it
        // from the executable location so installed runs never depend on the caller's CWD.
        applicationBaseDirectory ??= AppContext.BaseDirectory;
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        for (var current = new DirectoryInfo(applicationBaseDirectory); current is not null; current = current.Parent)
        {
            if (!seen.Add(current.FullName)) continue;
            if (TryFindInDirectory(Path.Combine(current.FullName, "config"), out configurationPath, out secretsPath)) return true;
        }
        return false;
    }

    private static bool TryFindInDirectory(string directory, out string configurationPath, out string secretsPath)
    {
        configurationPath = Path.Combine(directory, "slack.json");
        secretsPath = Path.Combine(directory, "slack.secrets.json");
        if (File.Exists(configurationPath)) return true;
        configurationPath = string.Empty;
        secretsPath = string.Empty;
        return false;
    }
}
