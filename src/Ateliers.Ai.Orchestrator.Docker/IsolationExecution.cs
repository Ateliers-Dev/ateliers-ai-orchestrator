using Ateliers.Ai.Orchestrator.Core;

namespace Ateliers.Ai.Orchestrator.Docker;

/// <summary>隔離実行へ渡す最小の役割別要求です。</summary>
public sealed record IsolationExecutionRequest(string RunId, string Role, int Attempt,
    string? ArtifactContent = null, string? ArtifactSha256 = null, TimeSpan? Timeout = null,
    RunStartGate? StartGate = null);

/// <summary>隔離実行結果の安全上の分類です。</summary>
public enum IsolationExecutionStatus
{
    /// <summary>終了コード0・終了・削除を確認しました。</summary>
    Succeeded,
    /// <summary>実行は終了しましたが正常終了しませんでした。</summary>
    Failed,
    /// <summary>隔離Agent操作が期限を超えました。</summary>
    TimedOut,
    /// <summary>呼び出し元から停止が要求されました。</summary>
    StopRequested,
    /// <summary>起動要求の結果を確認できません。</summary>
    StartUnconfirmed,
    /// <summary>プロセス終了を確認できません。</summary>
    TerminationUnconfirmed,
    /// <summary>コンテナー削除を確認できません。</summary>
    RemovalUnconfirmed
}

/// <summary>隔離実行器が観測した結果です。未確認の終了や削除は成功として扱いません。</summary>
public sealed record IsolationExecutionResult(
    IsolationExecutionStatus Status,
    int? ExitCode,
    bool TerminationConfirmed,
    bool ContainerRemovalConfirmed,
    bool DockerProcessesExitConfirmed,
    string StandardOutput,
    string StandardError,
    string? Error = null)
{
    /// <summary>Agentの後続処理へ進めるための最低条件を満たしたかを示します。</summary>
    public bool IsSafeToHandoff => Status == IsolationExecutionStatus.Succeeded && ExitCode == 0 && TerminationConfirmed &&
        ContainerRemovalConfirmed && DockerProcessesExitConfirmed;
}

/// <summary>Agent処理を実行基盤から独立して要求するCore境界です。</summary>
public interface IIsolationExecutor
{
    /// <summary>一つのFake Agent役割を隔離環境で実行します。</summary>
    Task<IsolationExecutionResult> ExecuteAsync(IsolationExecutionRequest request, CancellationToken cancellationToken = default);
}
