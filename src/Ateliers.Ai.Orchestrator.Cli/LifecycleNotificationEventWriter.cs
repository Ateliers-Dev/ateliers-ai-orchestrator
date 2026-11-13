using Ateliers.Ai.Orchestrator.Core;
using Ateliers.Slack;

namespace Ateliers.Ai.Orchestrator.Cli;

/// <summary>JSONL保存とSlack通知を独立して実行するライフサイクルWriterです。</summary>
internal sealed class LifecycleNotificationEventWriter : ILifecycleEventWriter, IDisposable
{
    private readonly JsonlLifecycleEventWriter jsonlWriter;
    private readonly SlackLifecycleNotificationDispatcher? slackDispatcher;

    public LifecycleNotificationEventWriter(string lifecycleLogPath, bool notificationsDisabled)
    {
        jsonlWriter = new JsonlLifecycleEventWriter(lifecycleLogPath);
        if (!notificationsDisabled && TryFindSlackSettings(out var configurationPath, out var secretsPath))
            slackDispatcher = SlackLifecycleNotificationDispatcher.TryCreate(configurationPath, secretsPath);
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
                if (!File.Exists(candidateConfiguration) || !File.Exists(candidateSecrets)) continue;
                configurationPath = candidateConfiguration;
                secretsPath = candidateSecrets;
                return true;
            }
        }
        return false;
    }
}
