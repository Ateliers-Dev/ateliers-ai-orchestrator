using Ateliers.Ai.Orchestrator.Core;
using Ateliers.Slack;

namespace Ateliers.Ai.Orchestrator.Cli;

/// <summary>JSONL保存とSlack通知を独立して実行するライフサイクルWriterです。</summary>
internal sealed class LifecycleNotificationEventWriter : ILifecycleEventWriter, IDisposable
{
    private readonly JsonlLifecycleEventWriter jsonlWriter;
    private readonly SlackLifecycleNotificationDispatcher? slackDispatcher;

    public LifecycleNotificationEventWriter(string lifecycleLogPath, bool notificationsDisabled)
        : this(lifecycleLogPath, notificationsDisabled, configurationPath: null, secretsPath: null, discoverSettings: true)
    {
    }

    internal LifecycleNotificationEventWriter(string lifecycleLogPath, bool notificationsDisabled,
        string configurationPath, string? secretsPath)
        : this(lifecycleLogPath, notificationsDisabled, configurationPath, secretsPath, discoverSettings: false)
    {
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
        }
        catch (Exception)
        {
            WriteWarning("警告: Slack通知の設定を読み込めないため、通知をスキップします。実行とJSONL記録は継続します。");
        }
    }

    public int FailedWrites => jsonlWriter.FailedWrites;
    public int FailedSlackSends => slackDispatcher?.FailedSends ?? 0;

    public bool TryAppend(LifecycleEvent item)
    {
        var recorded = jsonlWriter.TryAppend(item);
        slackDispatcher?.TrySend(item.RunId, item.Role, item.EventType, item.Status, item.State, item.TimestampUtc);
        return recorded;
    }

    public void Dispose() => slackDispatcher?.Dispose();

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
