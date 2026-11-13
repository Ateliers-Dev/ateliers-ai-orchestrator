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
    private int pendingNotifications;
    private int abandonedNotifications;
    private bool notificationAccountingFinalized;

    public LifecycleNotificationEventWriter(string lifecycleLogPath, bool notificationsDisabled)
        : this(lifecycleLogPath, notificationsDisabled, configurationPath: null, secretsPath: null, discoverSettings: true)
    {
    }

    internal LifecycleNotificationEventWriter(string lifecycleLogPath, bool notificationsDisabled,
        string configurationPath, string? secretsPath)
        : this(lifecycleLogPath, notificationsDisabled, configurationPath, secretsPath, discoverSettings: false)
    {
    }

    internal LifecycleNotificationEventWriter(string lifecycleLogPath, bool notificationsDisabled,
        Func<LifecycleEvent, CancellationToken, Task<bool>> notificationSender)
    {
        ArgumentNullException.ThrowIfNull(notificationSender);
        jsonlWriter = new JsonlLifecycleEventWriter(lifecycleLogPath);
        if (!notificationsDisabled) StartNotificationWorker(notificationSender);
    }

    private LifecycleNotificationEventWriter(string lifecycleLogPath, bool notificationsDisabled,
        string? configurationPath, string? secretsPath, bool discoverSettings)
    {
        jsonlWriter = new JsonlLifecycleEventWriter(lifecycleLogPath);
        if (notificationsDisabled) return;

        try
        {
            if (discoverSettings && !TryFindSlackSettings(out configurationPath, out secretsPath)) return;
            slackDispatcher = SlackLifecycleNotificationDispatcher.TryCreate(configurationPath ?? string.Empty, secretsPath ?? string.Empty, out var warning);
            if (warning is not null)
                WriteWarning($"警告: {warning}通知をスキップします。実行とJSONL記録は継続します。");
            if (slackDispatcher is not null)
                StartNotificationWorker(async (item, cancellationToken) =>
                {
                    return await slackDispatcher.TrySendAsync(item.RunId, item.Role, item.EventType, item.Status, item.State,
                        item.TimestampUtc, cancellationToken).ConfigureAwait(false);
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

    public bool TryAppend(LifecycleEvent item)
    {
        var recorded = jsonlWriter.TryAppend(item);
        if (slackQueue is { } queue)
        {
            Interlocked.Increment(ref pendingNotifications);
            if (!queue.Writer.TryWrite(item))
            {
                Interlocked.Decrement(ref pendingNotifications);
                Interlocked.Increment(ref droppedNotifications);
            }
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

    private void StartNotificationWorker(Func<LifecycleEvent, CancellationToken, Task<bool>> notificationSender)
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
                await foreach (var item in slackQueue.Reader.ReadAllAsync(slackCancellation.Token).ConfigureAwait(false))
                {
                    bool sent;
                    try
                    {
                        sent = await notificationSender(item, slackCancellation.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        sent = false;
                    }
                    CompleteNotification(sent);
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

    private void CompleteNotification(bool sent)
    {
        lock (notificationStateLock)
        {
            Interlocked.Decrement(ref pendingNotifications);
            if (!notificationAccountingFinalized && !sent)
                Interlocked.Increment(ref failedNotifications);
        }
    }

    private void CancelAndAccountPendingNotifications()
    {
        lock (notificationStateLock)
        {
            if (notificationAccountingFinalized) return;
            notificationAccountingFinalized = true;
            Interlocked.Add(ref abandonedNotifications, Volatile.Read(ref pendingNotifications));
        }
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
        var roots = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory };
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var root in roots)
        {
            for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
            {
                if (!seen.Add(current.FullName)) continue;
                var configDirectory = Path.Combine(current.FullName, "config");
                var candidateConfiguration = Path.Combine(configDirectory, "slack.json");
                var candidateSecrets = Path.Combine(configDirectory, "slack.secrets.json");
                if (!File.Exists(candidateConfiguration)) continue;
                configurationPath = candidateConfiguration;
                secretsPath = candidateSecrets;
                return true;
            }
        }
        return false;
    }
}
