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
    private Channel<LifecycleEvent>? slackQueue;
    private CancellationTokenSource? slackCancellation;
    private Task? slackWorker;
    private int droppedNotifications;
    private int unfinishedNotifications;

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
        Func<LifecycleEvent, CancellationToken, Task> notificationSender)
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
                    await slackDispatcher.TrySendAsync(item.RunId, item.Role, item.EventType, item.Status, item.State,
                        item.TimestampUtc, cancellationToken).ConfigureAwait(false);
                });
        }
        catch (Exception)
        {
            WriteWarning("警告: Slack通知の設定を読み込めないため、通知をスキップします。実行とJSONL記録は継続します。");
        }
    }

    public int FailedWrites => jsonlWriter.FailedWrites;
    public int FailedSlackSends => (slackDispatcher?.FailedSends ?? 0) + Volatile.Read(ref droppedNotifications) +
        Volatile.Read(ref unfinishedNotifications);

    public bool TryAppend(LifecycleEvent item)
    {
        var recorded = jsonlWriter.TryAppend(item);
        if (slackQueue is { } queue && !queue.Writer.TryWrite(item))
            Interlocked.Increment(ref droppedNotifications);
        return recorded;
    }

    public async Task FlushSlackNotificationsAsync(CancellationToken cancellationToken = default)
    {
        if (slackQueue is not { } queue || slackWorker is not { } worker) return;
        queue.Writer.TryComplete();
        if (cancellationToken.IsCancellationRequested) return;

        try
        {
            await worker.WaitAsync(NotificationDrainTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Interlocked.Increment(ref unfinishedNotifications);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation must not wait for the optional notification worker.
        }
    }

    public void Dispose()
    {
        slackQueue?.Writer.TryComplete();
        try { slackCancellation?.Cancel(); }
        catch (Exception) { }
        slackDispatcher?.Dispose();
        slackCancellation?.Dispose();
    }

    private void StartNotificationWorker(Func<LifecycleEvent, CancellationToken, Task> notificationSender)
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
                    await notificationSender(item, slackCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (slackCancellation.IsCancellationRequested)
            {
                // The workflow result is independent from notifications still queued at shutdown.
            }
            catch (Exception)
            {
                Interlocked.Increment(ref unfinishedNotifications);
            }
        });
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
