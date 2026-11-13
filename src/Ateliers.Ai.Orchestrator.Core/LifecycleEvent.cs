using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ateliers.Ai.Orchestrator.Core;

/// <summary>後続の通知・分析で利用できる、安全な実行ライフサイクルイベントです。</summary>
public sealed record LifecycleEvent
{
    public required string RunId { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public required string Role { get; init; }
    public required string EventType { get; init; }
    public required string Status { get; init; }
    public required string State { get; init; }
    public string? RelatedId { get; init; }
    public string? Summary { get; init; }
}

/// <summary>安全なライフサイクルイベントを受け取る境界です。</summary>
public interface ILifecycleEventWriter
{
    /// <summary>イベントを記録します。falseは記録に失敗したことを示します。</summary>
    bool TryAppend(LifecycleEvent item);
}

/// <summary>ライフサイクルイベントを追記専用JSONLへ保存します。</summary>
public sealed class JsonlLifecycleEventWriter(string path) : ILifecycleEventWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string fullPath = Path.GetFullPath(path);
    private int failedWrites;
    public int FailedWrites => Volatile.Read(ref failedWrites);

    public bool TryAppend(LifecycleEvent item)
    {
        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var line = JsonSerializer.Serialize(item, JsonOptions) + "\n";
            using var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            var bytes = Encoding.UTF8.GetBytes(line);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            Interlocked.Increment(ref failedWrites);
            return false;
        }
    }
}
