using System.Diagnostics;
using Ateliers.Ai.Orchestrator.Core;

namespace Ateliers.Ai.Orchestrator.Docker;

/// <summary>Docker CLIの一回の有期限実行結果です。DI seamはFake検証にも使用します。</summary>
public sealed record DockerCommandResult(int? ExitCode, bool ProcessExitObserved, bool TimedOut, bool StopRequested,
    string StandardOutput, string StandardError)
{
    public static DockerCommandResult StartFailed(string error) => new(null, false, false, false, "", error);
    public static DockerCommandResult StopBeforeStart() => new(null, false, false, true, "", "Stop requested before Docker CLI start.");
}

/// <summary>Docker CLI実行境界です。各呼出しは実装側のtimeoutを守る必要があります。</summary>
public interface IDockerCommandRunner
{
    Task<DockerCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>固定した制限でリポジトリ内のFake AgentだけをDockerコンテナーで実行します。</summary>
public sealed class DockerIsolationExecutor : IIsolationExecutor
{
    /// <summary>Fake Agentイメージの既定タグです。</summary>
    public const string DefaultImage = "ateliers-fake-isolation:2026-11-10";
    private static readonly TimeSpan DockerCommandTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProcessKillWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultFakeTimeout = TimeSpan.FromMinutes(2);
    private readonly IDockerCommandRunner commandRunner;

    /// <summary>Docker CLI runnerを差し替え可能にします。Fake検証では実Dockerを起動しません。</summary>
    public DockerIsolationExecutor(string dockerPath = "docker", IDockerCommandRunner? commandRunner = null)
    {
        this.commandRunner = commandRunner ?? new ProcessDockerCommandRunner(dockerPath, ProcessKillWait);
    }

    /// <inheritdoc />
    public async Task<IsolationExecutionResult> ExecuteAsync(IsolationExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var startGate = request.StartGate ?? new RunStartGate();
        using var stopRegistration = cancellationToken.Register(static state => ((RunStartGate)state!).RequestStop(), startGate);
        if (cancellationToken.IsCancellationRequested) startGate.RequestStop();
        if (startGate.IsStopRequested)
            return Failure(IsolationExecutionStatus.StopRequested, "コンテナー起動前に停止要求を受け付けました。");
        if (request.Role is not ("implementer" or "reviewer") || request.Attempt < 1 || string.IsNullOrWhiteSpace(request.RunId) ||
            (request.Role == "reviewer" && (request.ArtifactContent is null || request.ArtifactContent.Length > 4096 ||
                request.ArtifactSha256 is null || request.ArtifactSha256.Length != 64 || !request.ArtifactSha256.All(Uri.IsHexDigit))) ||
            (request.Role == "implementer" && (request.ArtifactContent is not null || request.ArtifactSha256 is not null)))
            return Failure(IsolationExecutionStatus.Failed, "隔離実行要求が不正です。");

        var fakeTimeout = request.Timeout is { } requestedTimeout && requestedTimeout < DefaultFakeTimeout
            ? requestedTimeout
            : DefaultFakeTimeout;
        if (fakeTimeout <= TimeSpan.Zero)
            return Failure(IsolationExecutionStatus.TimedOut, "Fake Agentの残り実行時間がありません。");

        var name = $"ateliers-fake-{Guid.NewGuid():N}";
        var labels = new[] { "--label", "ateliers.managed=true", "--label", $"ateliers.run={SafeLabel(request.RunId)}", "--label", $"ateliers.role={request.Role}", "--label", $"ateliers.attempt={request.Attempt}" };
        var created = await RunDockerAsync([
            "create", "--name", name, .. labels,
            "--network", "none", "--read-only",
            "--tmpfs", "/tmp:rw,noexec,nosuid,nodev,size=16m,uid=65532,gid=65532,mode=700",
            "--memory=128m", "--cpus=0.5", "--pids-limit=64", "--cap-drop=ALL",
            "--security-opt=no-new-privileges=true", "--user", "65532:65532", DefaultImage,
            request.Role, "readonly",
            .. (request.Role == "reviewer" ? new[] { request.ArtifactContent!, request.ArtifactSha256! } : Array.Empty<string>())
        ], CommandTimeout(fakeTimeout), cancellationToken, startGate);

        if (!created.ProcessExitObserved || created.ExitCode != 0 || string.IsNullOrWhiteSpace(created.StandardOutput))
        {
            var cleanup = await CleanupAsync(name);
            var status = ClassifyCommandFailure(created, IsolationExecutionStatus.StartUnconfirmed);
            return Result(status, null, cleanup.TerminationConfirmed, cleanup.RemovalConfirmed,
                created.ProcessExitObserved && cleanup.DockerProcessesExitConfirmed, created,
                "Docker container createの結果が不明です。後続起動と成果物handoffを停止しました。");
        }

        var containerId = created.StandardOutput.Trim();
        if (startGate.IsStopRequested)
        {
            var cleanup = await CleanupAsync(containerId);
            return Result(IsolationExecutionStatus.StopRequested, null, cleanup.TerminationConfirmed, cleanup.RemovalConfirmed,
                created.ProcessExitObserved && cleanup.DockerProcessesExitConfirmed, created,
                "起動前に停止要求を受け付けました。成果物handoffを停止しました。");
        }

        // This lock-protected commit is the launch linearization point. If stop wins, no start CLI is spawned;
        // if launch wins, a later stop is accepted and the bounded cleanup path runs.
        if (!startGate.TryBeginContainerStart(out _))
        {
            var cleanupBeforeStart = await CleanupAsync(containerId);
            return Result(IsolationExecutionStatus.StopRequested, null, cleanupBeforeStart.TerminationConfirmed,
                cleanupBeforeStart.RemovalConfirmed, created.ProcessExitObserved && cleanupBeforeStart.DockerProcessesExitConfirmed,
                DockerCommandResult.StopBeforeStart(), "Docker起動前に停止要求を受け付けました。成果物handoffを停止しました。");
        }
        var start = await RunDockerAsync(["start", "--attach", containerId], fakeTimeout, cancellationToken, startGate, startAlreadyCommitted: true);
        if (!start.ProcessExitObserved || start.TimedOut || start.StopRequested)
        {
            var cleanup = await CleanupAsync(containerId);
            var status = ClassifyCommandFailure(start, IsolationExecutionStatus.TerminationUnconfirmed);
            return Result(status, null, cleanup.TerminationConfirmed, cleanup.RemovalConfirmed,
                start.ProcessExitObserved && cleanup.DockerProcessesExitConfirmed, start,
                "Docker startの完了を確認できません。停止・終了・削除を確認し、後続起動とhandoffを停止しました。");
        }

        if (startGate.IsStopRequested || cancellationToken.IsCancellationRequested)
        {
            var cleanup = await CleanupAsync(containerId);
            return Result(IsolationExecutionStatus.StopRequested, null, cleanup.TerminationConfirmed, cleanup.RemovalConfirmed,
                created.ProcessExitObserved && start.ProcessExitObserved && cleanup.DockerProcessesExitConfirmed, start,
                "実行後の停止要求を受け付けました。成果物handoffを停止しました。");
        }

        var inspect = await RunDockerAsync(["inspect", "--format", "{{.State.Status}} {{.State.ExitCode}}", containerId], CommandTimeout(fakeTimeout), cancellationToken, startGate);
        var state = inspect.ProcessExitObserved && inspect.ExitCode == 0
            ? inspect.StandardOutput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];
        var terminated = state.Length == 2 && state[0] == "exited" && int.TryParse(state[1], out var inspectedExitCode) && inspectedExitCode == start.ExitCode;
        if (!terminated || startGate.IsStopRequested || cancellationToken.IsCancellationRequested)
        {
            var cleanup = await CleanupAsync(containerId);
            var status = startGate.IsStopRequested || cancellationToken.IsCancellationRequested ? IsolationExecutionStatus.StopRequested :
                inspect.TimedOut ? IsolationExecutionStatus.TimedOut : IsolationExecutionStatus.TerminationUnconfirmed;
            return Result(status, null, terminated || cleanup.TerminationConfirmed, cleanup.RemovalConfirmed,
                created.ProcessExitObserved && start.ProcessExitObserved && inspect.ProcessExitObserved && cleanup.DockerProcessesExitConfirmed, inspect,
                "Docker終了状態を確認できないか停止要求が確定しました。後続起動とhandoffを停止しました。");
        }

        var removal = await RunDockerAsync(["rm", containerId], CommandTimeout(fakeTimeout), cancellationToken, startGate);
        var afterRemoval = await RunDockerAsync(["inspect", "--format", "{{.Id}}", containerId], DockerCommandTimeout, CancellationToken.None, null);
        var removed = removal.ProcessExitObserved && removal.ExitCode == 0 &&
            string.Equals(removal.StandardOutput.Trim(), containerId, StringComparison.OrdinalIgnoreCase) &&
            IsConfirmedNotFound(afterRemoval);
        if (startGate.IsStopRequested || cancellationToken.IsCancellationRequested || !removed)
        {
            var cleanup = removed ? new CleanupResult(true, true, true) : await CleanupAsync(containerId);
            var status = startGate.IsStopRequested || cancellationToken.IsCancellationRequested ? IsolationExecutionStatus.StopRequested :
                removal.TimedOut ? IsolationExecutionStatus.TimedOut : IsolationExecutionStatus.RemovalUnconfirmed;
            return Result(status, null, true, removed || cleanup.RemovalConfirmed,
                created.ProcessExitObserved && start.ProcessExitObserved && inspect.ProcessExitObserved && removal.ProcessExitObserved &&
                    afterRemoval.ProcessExitObserved && cleanup.DockerProcessesExitConfirmed, removal,
                "コンテナー削除または停止要求の結果が未確認です。後続起動とhandoffを停止しました。");
        }

        var finalStatus = start.ExitCode == 0 ? IsolationExecutionStatus.Succeeded : IsolationExecutionStatus.Failed;
        return Result(finalStatus, start.ExitCode, true, true,
            created.ProcessExitObserved && start.ProcessExitObserved && inspect.ProcessExitObserved && removal.ProcessExitObserved && afterRemoval.ProcessExitObserved,
            start,
            finalStatus == IsolationExecutionStatus.Succeeded ? null : "Fake Agentが非ゼロ終了しました。後続起動を停止しました。");
    }

    private async Task<CleanupResult> CleanupAsync(string target)
    {
        // Every cleanup command has an independent bounded token; the already-cancelled Run token is never reused.
        // Even an inspect error, malformed response, or unknown state must reach stop before removal is considered.
        var before = await RunDockerAsync(["inspect", "--format", "{{.Id}} {{.State.Status}}", target], DockerCommandTimeout, CancellationToken.None, null);
        var stopped = await RunDockerAsync(["stop", "--time", "5", target], DockerCommandTimeout, CancellationToken.None, null);
        var afterStop = await RunDockerAsync(["inspect", "--format", "{{.Id}} {{.State.Status}}", target], DockerCommandTimeout, CancellationToken.None, null);
        var removed = await RunDockerAsync(["rm", target], DockerCommandTimeout, CancellationToken.None, null);
        var afterRemoval = await RunDockerAsync(["inspect", "--format", "{{.Id}}", target], DockerCommandTimeout, CancellationToken.None, null);

        var afterFields = ParseState(afterStop);
        var terminated = IsConfirmedNotFound(afterStop) || afterFields is { State: "exited" or "created" };
        var removalConfirmed = IsConfirmedNotFound(afterRemoval);
        var allDockerProcessesExited = before.ProcessExitObserved && stopped.ProcessExitObserved &&
            afterStop.ProcessExitObserved && removed.ProcessExitObserved && afterRemoval.ProcessExitObserved;
        return new(terminated, removalConfirmed, allDockerProcessesExited);
    }

    private async Task<DockerCommandResult> RunDockerAsync(IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken, RunStartGate? startGate, bool startAlreadyCommitted = false)
    {
        if (startGate is not null)
        {
            if (cancellationToken.IsCancellationRequested) startGate.RequestStop();
            var allowed = startAlreadyCommitted || (arguments.Count >= 3 && arguments[0] == "start"
                ? startGate.TryBeginContainerStart(out _)
                : startGate.TryBeginDockerOperation());
            if (!allowed) return DockerCommandResult.StopBeforeStart();
        }

        return await commandRunner.RunAsync(arguments, timeout, cancellationToken);
    }

    internal static async Task<DockerCommandResult> RunDockerProcessAsync(string dockerPath,
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, TimeSpan processKillWait)
    {
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo(dockerPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            process = Process.Start(start);
            if (process is null) return DockerCommandResult.StartFailed("Docker process did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            commandTimeout.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(commandTimeout.Token);
                return new(process.ExitCode, true, false, cancellationToken.IsCancellationRequested, await stdout, await stderr);
            }
            catch (OperationCanceledException)
            {
                var wasStopped = cancellationToken.IsCancellationRequested;
                var wasTimedOut = !wasStopped;
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }

                using var wait = new CancellationTokenSource(processKillWait);
                var processExitObserved = false;
                try { await process.WaitForExitAsync(wait.Token); processExitObserved = true; }
                catch (OperationCanceledException) { }
                if (!processExitObserved || !process.HasExited)
                    return new(null, false, wasTimedOut, wasStopped, "", "Docker CLI終了を確認できませんでした。");
                return new(process.ExitCode, true, wasTimedOut, wasStopped, await stdout, await stderr);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return DockerCommandResult.StartFailed(exception.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static (string Id, string State)? ParseState(DockerCommandResult command)
    {
        if (!command.ProcessExitObserved || command.ExitCode != 0) return null;
        var fields = command.StandardOutput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length == 2 && !string.IsNullOrWhiteSpace(fields[0])
            ? (fields[0], fields[1]) : null;
    }

    private static IsolationExecutionStatus ClassifyCommandFailure(DockerCommandResult command, IsolationExecutionStatus unknown) =>
        command.StopRequested ? IsolationExecutionStatus.StopRequested :
        command.TimedOut ? IsolationExecutionStatus.TimedOut : unknown;

    private static bool IsConfirmedNotFound(DockerCommandResult command) => command.ProcessExitObserved && command.ExitCode != 0 &&
        (command.StandardError.Contains("No such object:", StringComparison.OrdinalIgnoreCase) ||
         command.StandardError.Contains("no such object:", StringComparison.OrdinalIgnoreCase));

    private static string SafeLabel(string value) => new(value.Where(char.IsAsciiLetterOrDigit).Take(32).ToArray());
    private static TimeSpan CommandTimeout(TimeSpan fakeTimeout) => fakeTimeout < DockerCommandTimeout ? fakeTimeout : DockerCommandTimeout;
    private static IsolationExecutionResult Failure(IsolationExecutionStatus status, string error) =>
        new(status, null, false, false, true, "", "", error);
    private static IsolationExecutionResult Result(IsolationExecutionStatus status, int? exitCode, bool terminated, bool removed,
        bool dockerProcessesExitConfirmed, DockerCommandResult command, string? error) =>
        new(status, exitCode, terminated, removed, dockerProcessesExitConfirmed, command.StandardOutput, command.StandardError, error);
    private sealed record CleanupResult(bool TerminationConfirmed, bool RemovalConfirmed, bool DockerProcessesExitConfirmed);
}

internal sealed class ProcessDockerCommandRunner(string dockerPath, TimeSpan processKillWait) : IDockerCommandRunner
{
    public Task<DockerCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
        DockerIsolationExecutor.RunDockerProcessAsync(dockerPath, arguments, timeout, cancellationToken, processKillWait);
}
