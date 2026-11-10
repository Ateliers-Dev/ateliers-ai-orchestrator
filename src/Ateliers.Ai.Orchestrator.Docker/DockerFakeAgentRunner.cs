using System.Security.Cryptography;
using System.Text;
using Ateliers.Ai.Orchestrator.Core;

namespace Ateliers.Ai.Orchestrator.Docker;

/// <summary>既存Fake workflowへ、Dockerで実行したFake結果だけを接続します。</summary>
public sealed class DockerFakeAgentRunner(IIsolationExecutor executor) : IAgentRunner
{
    private static readonly TimeSpan MaxFakeTimeout = TimeSpan.FromMinutes(2);

    /// <inheritdoc />
    public AgentResponse Implement(AgentRequest request)
    {
        var result = Execute(request, "implementer", null, null);
        if (!result.IsSafeToHandoff || !result.StandardOutput.Contains("fake_result=pass", StringComparison.Ordinal))
            return FailureResponse(result);
        return new(result.StandardOutput, SelfAssessment: "合成Fakeの正常終了を確認しました。");
    }

    /// <inheritdoc />
    public AgentResponse Review(AgentRequest request)
    {
        if (request.RunDirectory is null || request.ArtifactReference is null || request.ArtifactSha256 is null)
            return new("固定成果物を解決できませんでした。", Succeeded: false, StopRequested: true);
        try
        {
            var runRoot = Path.GetFullPath(request.RunDirectory);
            var artifactPath = Path.GetFullPath(Path.Combine(runRoot, request.ArtifactReference));
            if (!artifactPath.StartsWith(runRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(artifactPath))
                return new("固定成果物のパス検証に失敗しました。", Succeeded: false, StopRequested: true);
            var bytes = File.ReadAllBytes(artifactPath);
            var actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(actualHash, request.ArtifactSha256, StringComparison.OrdinalIgnoreCase))
                return new("固定成果物のハッシュ検証に失敗しました。", Succeeded: false, StopRequested: true);
            var content = new UTF8Encoding(false, true).GetString(bytes);
            if (content.Length > 4096)
                return new("PoCのFake入力上限を超えました。", Succeeded: false, StopRequested: true);
            var result = Execute(request, "reviewer", content, actualHash);
            if (!result.IsSafeToHandoff || !result.StandardOutput.Contains($"review_artifact_sha256={actualHash}", StringComparison.Ordinal) ||
                !result.StandardOutput.Contains("review_artifact_hash=verified", StringComparison.Ordinal) ||
                !result.StandardOutput.Contains("role_a_canary_visible=no", StringComparison.Ordinal) ||
                !result.StandardOutput.Contains("fake_result=pass", StringComparison.Ordinal))
                return FailureResponse(result);
            return new("レビュー合格: Docker FakeはImplementerの固定成果物ハッシュを検証しました。 role_a_canary_visible=no");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return new($"固定成果物の読取に失敗しました ({exception.GetType().Name})。", Succeeded: false, StopRequested: true);
        }
    }

    private IsolationExecutionResult Execute(AgentRequest request, string role, string? artifactContent, string? artifactHash)
    {
        var startGate = request.StartGate ?? new RunStartGate();
        if (request.StopToken.IsCancellationRequested) startGate.RequestStop();
        if (startGate.IsStopRequested)
            return new(IsolationExecutionStatus.StopRequested, null, false, false, true, "", "", "起動前に停止要求を受け付けました。");
        var timeout = request.RemainingTime is { } remaining && remaining < MaxFakeTimeout ? remaining : MaxFakeTimeout;
        if (timeout <= TimeSpan.Zero)
            return new(IsolationExecutionStatus.TimedOut, null, false, false, true, "", "", "Fake Agentの残り実行時間がありません。");
        try
        {
            return executor.ExecuteAsync(new(request.RunId, role, request.Attempt, artifactContent, artifactHash, timeout, startGate), request.StopToken)
                .GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            return new(IsolationExecutionStatus.StartUnconfirmed, null, false, false, false, "", "",
                $"隔離実行の結果が不明です ({exception.GetType().Name})。");
        }
    }

    private static AgentResponse FailureResponse(IsolationExecutionResult result)
    {
        var diagnostic = string.Join(Environment.NewLine,
            $"status={result.Status}; exitCode={result.ExitCode?.ToString() ?? "unobserved"}; " +
                $"terminationConfirmed={result.TerminationConfirmed}; removalConfirmed={result.ContainerRemovalConfirmed}; " +
                $"dockerProcessesExitConfirmed={result.DockerProcessesExitConfirmed}; timedOut={result.Status == IsolationExecutionStatus.TimedOut}; " +
                $"errorPresent={!string.IsNullOrEmpty(result.Error)}; stdoutChars={result.StandardOutput.Length}; stderrChars={result.StandardError.Length}",
            $"stage={SafeFailureStage(result.Status)}");
        return new(diagnostic, Succeeded: false,
            TimedOut: result.Status == IsolationExecutionStatus.TimedOut,
            StopRequested: true);
    }

    private static string SafeFailureStage(IsolationExecutionStatus status) => status switch
    {
        IsolationExecutionStatus.TimedOut => "timeout",
        IsolationExecutionStatus.StartUnconfirmed => "start-unconfirmed",
        IsolationExecutionStatus.TerminationUnconfirmed => "termination-unconfirmed",
        IsolationExecutionStatus.RemovalUnconfirmed => "removal-unconfirmed",
        IsolationExecutionStatus.StopRequested => "stop-requested",
        IsolationExecutionStatus.Failed => "execution-failed",
        _ => "handoff-rejected"
    };
}
