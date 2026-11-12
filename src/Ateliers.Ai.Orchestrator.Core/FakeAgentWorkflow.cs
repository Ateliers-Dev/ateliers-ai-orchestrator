using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Ateliers.Ai.Orchestrator.Core;

/// <summary>Agent呼び出しへ渡す、固定済みの役割別入力です。</summary>
/// <param name="RunId">実行識別子です。</param>
/// <param name="Role">呼び出す役割です。</param>
/// <param name="Contract">検証済みタスク契約です。</param>
/// <param name="Attempt">役割ごとの1始まりの試行番号です。</param>
/// <param name="ArtifactReference">レビュー時に固定して渡す成果物の参照です。</param>
/// <param name="ArtifactSha256">レビュー対象の固定バイト列のSHA-256です。</param>
/// <param name="ReviewFinding">修正依頼で実装側に渡すレビュー指摘です。</param>
public sealed record AgentRequest(
    string RunId,
    string Role,
    TaskContract Contract,
    int Attempt,
    string? ArtifactReference = null,
    string? ArtifactSha256 = null,
    string? ReviewFinding = null,
    string? RunDirectory = null,
    TimeSpan? RemainingTime = null,
    CancellationToken StopToken = default);

/// <summary>Agent呼び出しの結果と、実行環境が観測できた範囲を表します。</summary>
/// <param name="Content">Agentの応答本文です。</param>
/// <param name="Succeeded">呼び出しが成功したかを示します。</param>
/// <param name="TimedOut">タイムアウトが観測されたかを示します。</param>
/// <param name="SelfAssessment">役割自身による自己評価です。レビュー合格の根拠には使いません。</param>
public sealed record AgentResponse(string Content, bool Succeeded = true, bool TimedOut = false,
    string? SelfAssessment = null, AgentExecutionEvidence? ExecutionEvidence = null, bool StopRequested = false);

/// <summary>Agentプロセスから取得できた実行識別子と終了証跡です。</summary>
/// <param name="ProcessId">起動したプロセスのIDです。</param>
/// <param name="ThreadId">CLIイベントから取得したthread IDです。</param>
/// <param name="CliVersion">CLI版です。PoCでは起動前確認を行わないため未取得です。</param>
/// <param name="TerminalEvent">最後に観測したterminal eventです。</param>
/// <param name="ExitCode">取得できた終了コードです。</param>
/// <param name="UsageJson">取得できたtoken usage JSONです。金額や請求額を表しません。</param>
/// <param name="StandardOutputReference">生JSONLの保存先です。</param>
/// <param name="StandardErrorReference">標準エラーの保存先です。</param>
/// <param name="TimedOut">期限超過で終了を試みたかを示します。</param>
/// <param name="StopRequested">timeoutまたは利用者停止要求を受け付けたかを示します。</param>
/// <param name="StopReason">停止要求の由来です。</param>
/// <param name="KillAttempted">プロセスツリーの終了を試みたかを示します。</param>
/// <param name="ProcessExitObserved">終了後にプロセス終了を確認できたかを示します。</param>
/// <param name="StreamsClosed">標準出力・標準エラーの読取終了を確認できたかを示します。</param>
/// <param name="RequestSha256">実際のプロンプトのSHA-256です。本文は保存しません。</param>
/// <param name="WorkingDirectory">プロセスへ設定した作業ディレクトリです。</param>
/// <param name="ArtifactSha256">Reviewerへ渡した固定成果物のSHA-256です。</param>
/// <param name="Error">起動・解析・停止で取得したエラーです。</param>
public sealed record AgentExecutionEvidence(int? ProcessId, string? ThreadId, string? CliVersion,
    string? TerminalEvent, int? ExitCode, string? UsageJson, string? StandardOutputReference,
    string? StandardErrorReference, bool TimedOut, bool KillAttempted, bool? ProcessExitObserved,
    bool? StreamsClosed, bool StopRequested, string? StopReason, string? RequestSha256, string? WorkingDirectory, string? ArtifactSha256,
    string? Error = null);

/// <summary>ImplementerとReviewerを呼び出す差し替え可能な境界です。</summary>
public interface IAgentRunner
{
    /// <summary>実装または指摘対応を一回実行します。</summary>
    /// <param name="request">固定したタスクと修正指摘です。</param>
    /// <returns>実装結果です。</returns>
    AgentResponse Implement(AgentRequest request);

    /// <summary>固定成果物をレビューします。</summary>
    /// <param name="request">成果物の参照とハッシュを含むレビュー入力です。</param>
    /// <returns>レビュー結果です。</returns>
    AgentResponse Review(AgentRequest request);
}

/// <summary>固定応答を返すFake Agent実行のシナリオです。</summary>
public enum FakeScenario
{
    /// <summary>初回レビューで合格します。</summary>
    Success,
    /// <summary>指摘を一度修正した後、再レビューで合格します。</summary>
    RevisionThenSuccess,
    /// <summary>人間の判断が必要なレビュー結果を返します。</summary>
    Stop,
    /// <summary>Implementerが失敗します。</summary>
    ImplementerFailure,
    /// <summary>Reviewerが失敗します。</summary>
    ReviewerFailure,
    /// <summary>Agentが空応答を返します。</summary>
    EmptyResponse,
    /// <summary>Reviewerが空応答を返します。</summary>
    ReviewerEmptyResponse,
    /// <summary>Agentがタイムアウトします。</summary>
    Timeout,
    /// <summary>Reviewerがタイムアウトします。</summary>
    ReviewerTimeout
}

/// <summary>固定シナリオで再現可能なImplementerとReviewerを提供します。</summary>
public sealed class FakeAgentRunner(FakeScenario scenario) : IAgentRunner
{
    /// <inheritdoc />
    public AgentResponse Implement(AgentRequest request) => scenario switch
    {
        FakeScenario.ImplementerFailure => new("実装処理に失敗しました。", Succeeded: false),
        FakeScenario.EmptyResponse => new(" "),
        FakeScenario.Timeout => new("タイムアウトしました。", TimedOut: true),
        _ when request.Attempt == 1 => new("成果物の初版を作成しました。", SelfAssessment: "実装者の自己評価: 完了"),
        _ => new("レビュー指摘を反映した成果物を作成しました。", SelfAssessment: "実装者の自己評価: 修正済み")
    };

    /// <inheritdoc />
    public AgentResponse Review(AgentRequest request)
    {
        if (scenario == FakeScenario.ReviewerFailure) return new("レビュー処理に失敗しました。", Succeeded: false);
        if (scenario is FakeScenario.Timeout or FakeScenario.ReviewerTimeout) return new("タイムアウトしました。", TimedOut: true);
        if (scenario is FakeScenario.EmptyResponse or FakeScenario.ReviewerEmptyResponse) return new(" ");
        if (scenario == FakeScenario.Stop) return new("要件に判断が必要です: 既存成果物の変更範囲を特定できません。", SelfAssessment: "要確認");
        if (scenario == FakeScenario.RevisionThenSuccess && request.Attempt == 1)
            return new("指摘: 入力値の検証結果を明示してください。", SelfAssessment: "修正が必要");
        return new("レビュー合格: 固定成果物を検査し、阻害指摘はありません。", SelfAssessment: "合格");
    }
}

/// <summary>Fakeワークフローのファイル永続化境界です。</summary>
public interface IFakeWorkflowStorage
{
    void WriteAllText(string path, string content, Encoding encoding);
    byte[] ReadAllBytes(string path);
}

/// <summary>Agentによる一実行と、成果物・イベント・Markdown報告の保存をまとめます。</summary>
public sealed class FakeAgentWorkflow(IFakeWorkflowStorage? storage = null)
{
    private readonly IFakeWorkflowStorage storage = storage ?? new PhysicalFakeWorkflowStorage();
    /// <summary>契約を実行し、修正・再レビューの上限を守って終端状態を記録します。</summary>
    /// <param name="contract">検証済みのタスク契約です。</param>
    /// <param name="contractSha256">契約JSONのSHA-256です。</param>
    /// <param name="runId">一意な実行識別子です。</param>
    /// <param name="runDirectory">新規作成済みの実行保存先です。</param>
    /// <param name="agentRunner">差し替え可能なAgent実行境界です。</param>
    /// <param name="eventWriter">イベントを永続化する記録先です。</param>
    /// <param name="reviewEvidence">すべての外部証跡を別経路で取得できた場合のみ渡します。</param>
    /// <returns>最終状態、成果物ハッシュ、試行数、報告先を返します。</returns>
    /// <exception cref="IOException">実行開始前に保存先を準備できない場合に発生します。</exception>
    public FakeWorkflowResult Run(TaskContract contract, string contractSha256, string runId, string runDirectory,
        IAgentRunner agentRunner, IRunEventWriter eventWriter, HumanReviewEvidence? reviewEvidence = null,
        CancellationToken stopToken = default, bool codexOneShot = false)
    {
        Directory.CreateDirectory(runDirectory);
        var existingEntries = Directory.EnumerateFileSystemEntries(runDirectory).ToArray();
        var onlyNewEventLog = existingEntries.Length == 1 &&
            Path.GetFileName(existingEntries[0]).Equals("events.jsonl", StringComparison.OrdinalIgnoreCase) &&
            new FileInfo(existingEntries[0]).Length == 0;
        if (existingEntries.Length > 0 && !onlyNewEventLog)
            throw new IOException($"実行保存先に既存ファイルがあるため、上書きを防いで停止しました: {runDirectory}");
        Directory.CreateDirectory(Path.Combine(runDirectory, "artifacts"));
        Directory.CreateDirectory(Path.Combine(runDirectory, "exchanges"));
        var run = new RunExecution(eventWriter, contract);
        var stopwatch = Stopwatch.StartNew();
        var revisionAttempts = 0;
        var retryAttempts = 0;
        string? currentArtifact = null;
        string? currentHash = null;
        string? lastFinding = null;
        string? failure = null;
        string? oneShotReviewOutcome = null;
        var reviewAttempt = 0;
        var implementationAttempt = 0;
        var executionEvidence = new List<(string Role, int Attempt, AgentExecutionEvidence Evidence)>();
        var persistenceFailed = false;

        var isFake = agentRunner is FakeAgentRunner;
        try
        {
            eventWriter.Append("run_started", "accepted", to: RunState.Prepared,
                detail: isFake ? "Fake Agentシナリオを開始しました。実Agent・GitHub操作は行いません。" :
                    "実Agent実行を開始しました。GitHub操作は行いません。",
                inputReference: $"sha256:{contractSha256}",
                role: "Orchestrator");
            run.Transition(RunState.Implementing);
            while (true)
            {
                if (stopwatch.Elapsed >= TimeSpan.FromMinutes(contract.Limits.MaxDurationMinutes))
                {
                    failure = $"実行時間上限 {contract.Limits.MaxDurationMinutes} 分に到達しました。";
                    run.Transition(RunState.NeedsHumanInput);
                    break;
                }

                implementationAttempt++;
                var implementRequest = new AgentRequest(runId, "Implementer", contract, implementationAttempt,
                    ReviewFinding: lastFinding, RunDirectory: runDirectory,
                    RemainingTime: TimeSpan.FromMinutes(contract.Limits.MaxDurationMinutes) - stopwatch.Elapsed,
                    StopToken: stopToken);
                if (StopWasRequested(stopToken))
                {
                    failure = "Implementer起動前に停止要求を受け付けました。";
                    run.Transition(RunState.NeedsHumanInput);
                    break;
                }
                var implementResponse = InvokeAgent(() => agentRunner.Implement(implementRequest));
                if (StopWasRequested(stopToken))
                    implementResponse = implementResponse with { Succeeded = false, StopRequested = true };
                if (implementResponse.ExecutionEvidence is { } implementEvidence)
                    executionEvidence.Add(("Implementer", implementationAttempt, implementEvidence));
                var implementRef = SaveExchange(runDirectory, "implementer", implementationAttempt, "request.json",
                    System.Text.Json.JsonSerializer.Serialize(new { role = "Implementer", attempt = implementationAttempt,
                        taskId = contract.TaskId, workingDirectory = contract.WorkingDirectory,
                        reviewFinding = lastFinding, contractSha256 }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                var implementResponseRef = SaveExchange(runDirectory, "implementer", implementationAttempt, "response.md", implementResponse.Content);
                if (implementResponse.SelfAssessment is not null)
                    SaveExchange(runDirectory, "implementer", implementationAttempt, "self-assessment.md", implementResponse.SelfAssessment);
                SaveExecutionEvidence(runDirectory, "implementer", implementationAttempt, implementResponse.ExecutionEvidence);
                eventWriter.Append("agent_call", Outcome(implementResponse), from: run.State, to: run.State,
                    detail: "Implementer応答を記録しました。自己評価はレビュー判定に利用しません。",
                    inputReference: implementRef, outputReference: implementResponseRef,
                    elapsedMilliseconds: stopwatch.ElapsedMilliseconds, role: "Implementer");
                if (!implementResponse.Succeeded || implementResponse.StopRequested || implementResponse.TimedOut || string.IsNullOrWhiteSpace(implementResponse.Content))
                {
                    if (!codexOneShot && !implementResponse.TimedOut && !implementResponse.StopRequested && retryAttempts < contract.Limits.MaxRetries)
                    {
                        retryAttempts++;
                        eventWriter.Append("retry_scheduled", "accepted", from: run.State, to: run.State,
                            detail: $"Implementer再試行 {retryAttempts}/{contract.Limits.MaxRetries}", role: "Orchestrator");
                        continue;
                    }
                    failure = AgentFailure("Implementer", implementResponse);
                    run.Transition(codexOneShot || implementResponse.TimedOut || implementResponse.StopRequested ? RunState.NeedsHumanInput : RunState.Failed);
                    break;
                }

                currentArtifact = Path.Combine("artifacts", $"artifact-{implementationAttempt}.md");
                var fullArtifactPath = Path.Combine(runDirectory, currentArtifact);
                storage.WriteAllText(fullArtifactPath, implementResponse.Content + Environment.NewLine, new UTF8Encoding(false));
                var fixedBytes = storage.ReadAllBytes(fullArtifactPath);
                currentHash = Convert.ToHexStringLower(SHA256.HashData(fixedBytes));
                eventWriter.Append("artifact_fixed", "accepted", from: run.State, to: run.State,
                    detail: "レビュー対象を保存し、保存済みの同一バイト列からSHA-256を計算しました。",
                    outputReference: currentArtifact, diffSha256: currentHash, role: "Orchestrator");

                run.Transition(RunState.Reviewing);
                lastFinding = null;
                for (var attempt = 1; attempt <= (codexOneShot ? 1 : contract.Limits.MaxRetries + 1); attempt++)
                {
                    if (stopwatch.Elapsed >= TimeSpan.FromMinutes(contract.Limits.MaxDurationMinutes))
                    {
                        failure = $"実行時間上限 {contract.Limits.MaxDurationMinutes} 分に到達しました。";
                        run.Transition(RunState.NeedsHumanInput);
                        break;
                    }
                    reviewAttempt++;
                    // 初回レビュー要求にImplementerの自己評価を含めない。
                    var reviewRequest = new AgentRequest(runId, "Reviewer", contract, reviewAttempt,
                        currentArtifact, currentHash, RunDirectory: runDirectory,
                        RemainingTime: TimeSpan.FromMinutes(contract.Limits.MaxDurationMinutes) - stopwatch.Elapsed,
                        StopToken: stopToken);
                    if (StopWasRequested(stopToken))
                    {
                        failure = "Reviewer起動前に停止要求を受け付けました。成果物をhandoffしません。";
                        run.Transition(RunState.NeedsHumanInput);
                        break;
                    }
                    var reviewResponse = InvokeAgent(() => agentRunner.Review(reviewRequest));
                    if (StopWasRequested(stopToken))
                        reviewResponse = reviewResponse with { Succeeded = false, StopRequested = true };
                    oneShotReviewOutcome = !reviewResponse.Succeeded || reviewResponse.StopRequested || reviewResponse.TimedOut || string.IsNullOrWhiteSpace(reviewResponse.Content)
                        ? Outcome(reviewResponse)
                        : reviewResponse.Content.StartsWith("レビュー合格:", StringComparison.Ordinal) ? "合格"
                        : reviewResponse.Content.StartsWith("要件に判断が必要です:", StringComparison.Ordinal) ? "人間判断が必要"
                        : "指摘";
                    if (reviewResponse.ExecutionEvidence is { } reviewExecutionEvidence)
                        executionEvidence.Add(("Reviewer", reviewAttempt, reviewExecutionEvidence));
                    var reviewRef = SaveExchange(runDirectory, "reviewer", reviewAttempt, "request.json",
                        System.Text.Json.JsonSerializer.Serialize(new { role = "Reviewer", attempt = reviewAttempt,
                            workingDirectory = runDirectory, artifactReference = currentArtifact,
                            artifactSha256 = currentHash, contractSha256 }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    var reviewResponseRef = SaveExchange(runDirectory, "reviewer", reviewAttempt, "response.md", reviewResponse.Content);
                    SaveExecutionEvidence(runDirectory, "reviewer", reviewAttempt, reviewResponse.ExecutionEvidence);
                    eventWriter.Append("agent_call", Outcome(reviewResponse), from: run.State, to: run.State,
                        detail: "Reviewer応答を固定成果物参照と対応づけて記録しました。",
                        inputReference: reviewRef, outputReference: reviewResponseRef,
                        diffSha256: currentHash, elapsedMilliseconds: stopwatch.ElapsedMilliseconds, role: "Reviewer");

                    if (!reviewResponse.Succeeded || reviewResponse.StopRequested || reviewResponse.TimedOut || string.IsNullOrWhiteSpace(reviewResponse.Content))
                    {
                        if (!codexOneShot && !reviewResponse.StopRequested && !reviewResponse.TimedOut && attempt <= contract.Limits.MaxRetries && retryAttempts < contract.Limits.MaxRetries)
                        {
                            retryAttempts++;
                            eventWriter.Append("retry_scheduled", "accepted", from: run.State, to: run.State,
                                detail: $"Reviewer再試行 {retryAttempts}/{contract.Limits.MaxRetries}",
                                role: "Orchestrator");
                            continue;
                        }
                        failure = AgentFailure("Reviewer", reviewResponse);
                        run.Transition(codexOneShot || reviewResponse.TimedOut || reviewResponse.StopRequested ? RunState.NeedsHumanInput : RunState.Failed);
                        break;
                    }

                    if (reviewResponse.Content.StartsWith("レビュー合格:", StringComparison.Ordinal))
                    {
                        if (codexOneShot)
                        {
                            failure = "Reviewerは合格しました。PoC完了地点で人間判断待ちに停止しました（受入・PR証跡は未確認）。";
                            run.Transition(RunState.NeedsHumanInput);
                            break;
                        }
                        var evidence = reviewEvidence;
                        var expectedCriteriaSha256 = Convert.ToHexStringLower(SHA256.HashData(
                            Encoding.UTF8.GetBytes(string.Join("\n", contract.AcceptanceCriteria))));
                        if (evidence is not null && string.Equals(evidence.ArtifactSha256, currentHash, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(evidence.AcceptanceCriteriaSha256, expectedCriteriaSha256, StringComparison.OrdinalIgnoreCase) &&
                            evidence.AcceptanceCriteriaCount == contract.AcceptanceCriteria.Count &&
                            evidence.PassedAcceptanceCriteriaCount == contract.AcceptanceCriteria.Count)
                        {
                            try { run.CompleteForHumanReview(evidence); }
                            catch (InvalidRunTransitionException exception)
                            {
                                failure = exception.Message;
                                run.Transition(RunState.NeedsHumanInput);
                            }
                        }
                        else
                        {
                            failure = "Fakeレビューは完了しましたが、独立レビュー記録・全受け入れ条件の検証・実PR作成記録が揃っていません。PR提出とその証跡はIssue #5以降で扱います。";
                            run.Transition(RunState.NeedsHumanInput);
                        }
                        break;
                    }

                    if (reviewResponse.Content.StartsWith("要件に判断が必要です:", StringComparison.Ordinal))
                    {
                        failure = reviewResponse.Content;
                        run.Transition(RunState.NeedsHumanInput);
                        break;
                    }

                    lastFinding = reviewResponse.Content;
                    if (codexOneShot)
                    {
                        failure = $"Reviewerの指摘を記録しました。自動修正・再レビューは行わず人間判断待ちで停止します: {lastFinding}";
                        run.Transition(RunState.NeedsHumanInput);
                        break;
                    }
                    if (revisionAttempts >= contract.Limits.MaxRevisionAttempts)
                    {
                        failure = $"修正上限 {contract.Limits.MaxRevisionAttempts} 回に到達しました。最後の指摘: {lastFinding}";
                        run.Transition(RunState.NeedsHumanInput);
                        break;
                    }

                    revisionAttempts++;
                    run.Transition(RunState.Revising);
                    eventWriter.Append("revision_started", "accepted", from: run.State, to: run.State,
                        detail: $"修正試行 {revisionAttempts}/{contract.Limits.MaxRevisionAttempts}",
                        inputReference: reviewResponseRef, diffSha256: currentHash, role: "Orchestrator");
                    run.Transition(RunState.Implementing);
                    break;
                }
                if (run.State is RunState.ReadyForHumanReview or RunState.NeedsHumanInput or RunState.Failed) break;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure = $"保存失敗のため人間判断待ちで停止しました。追加Agentは起動していません。永続化状態は個別に確認してください。 ({exception.GetType().Name})";
            persistenceFailed = true;
            run.StopInMemoryAfterPersistenceFailure();
        }

        var persistenceSucceeded = !persistenceFailed;
        if (codexOneShot)
        {
            var reviewerOutcome = oneShotReviewOutcome ?? "未起動";
            var stopEventSaved = TryPersist(() => eventWriter.Append("codex_one_shot_stopped", "needs_human_input", from: run.State, to: run.State,
                detail: $"Implementer呼び出し={implementationAttempt}; Reviewer呼び出し={reviewAttempt}; 成果物SHA-256={currentHash ?? "未作成"}; Reviewer結果={reviewerOutcome}; 停止理由={failure ?? "実行を終了し人間判断待ち"}",
                diffSha256: currentHash, role: "Orchestrator"));
            persistenceSucceeded &= stopEventSaved;
            if (!stopEventSaved)
                failure = AppendFailure(failure, "停止イベント codex_one_shot_stopped の保存に失敗しました。");
        }

        var reportPath = Path.Combine(runDirectory, "report.md");
        var reportLines = new List<string>
        {
            isFake ? "# Fake Agent 実行結果" : "# Codex CLI 実行結果", "",
            $"- 実行ID: `{runId}`", $"- タスクID: `{contract.TaskId}`", $"- 状態: `{run.State}`",
            $"- 実装呼び出し: {implementationAttempt}", $"- レビュー呼び出し: {reviewAttempt}",
            $"- 修正回数: {revisionAttempts}/{contract.Limits.MaxRevisionAttempts}",
            $"- 再試行回数: {retryAttempts}/{contract.Limits.MaxRetries}",
            $"- 成果物: `{currentArtifact ?? "未作成"}`",
            $"- 成果物SHA-256: `{currentHash ?? "未取得"}`",
            $"- PR提出: {(run.State == RunState.ReadyForHumanReview ? reviewEvidence!.PullRequestUrl : "未実施・未検証")}",
            "- 人間のマージ: 未実施", isFake ? "- Agent: 未実施（Fake Agentのみ）" : "- Agent: Codex CLI子プロセス",
            $"- 結果・停止理由: {failure ?? (isFake ? "Fakeレビュー合格。PR操作証跡と受け入れ証跡がある場合のみ人間確認待ちに進みます。" : "Codex CLIレビュー合格。受け入れ検証とPR作成の証跡は別途必要です。")}",
            "", "## Agentプロセス証跡", ""
        };
        if (executionEvidence.Count == 0) reportLines.Add("- 実プロセス識別子・CLI版・token usage: 未取得（Fake実行）");
        foreach (var (role, attempt, evidence) in executionEvidence)
        {
            reportLines.Add($"- {role} #{attempt}: PID={evidence.ProcessId?.ToString() ?? "未取得"}; thread={evidence.ThreadId ?? "未取得"}; CLI={evidence.CliVersion ?? "未取得"}; terminal={evidence.TerminalEvent ?? "未取得"}; exit={evidence.ExitCode?.ToString() ?? "未取得"}");
            reportLines.Add($"  - 作業領域: `{evidence.WorkingDirectory ?? "未取得"}`; 要求SHA-256: `{evidence.RequestSha256 ?? "未取得"}`; 対象SHA-256: `{evidence.ArtifactSha256 ?? "初回実装"}`");
            reportLines.Add($"  - token usage JSON: `{evidence.UsageJson ?? "未取得"}`（金額・請求額・硬い予算上限ではありません）");
            reportLines.Add($"  - stdout: `{evidence.StandardOutputReference ?? "未取得"}`; stderr: `{evidence.StandardErrorReference ?? "未取得"}`; stopRequested={evidence.StopRequested} ({evidence.StopReason ?? "なし"}); timeout={evidence.TimedOut}; killAttempted={evidence.KillAttempted}; processExitObserved={evidence.ProcessExitObserved?.ToString() ?? "未確認"}; streamsClosed={evidence.StreamsClosed?.ToString() ?? "未確認"}");
            if (evidence.Error is not null) reportLines.Add($"  - エラー: {evidence.Error}");
        }
        reportLines.AddRange(["", "## 未取得・未保証", "", "- 実費・請求額・硬い予算上限: 未取得", "- 子Agent/再委譲の完全な観測・無効化: 未検証", "- GitHubへの書込・PR作成: AgentRunnerでは未実施", "- CLIプロセスkillはAgent turnの中断保証ではありません。", "", "ローカル実行記録は耐改ざん・完全監査の台帳ではありません。", ""]);
        var report = string.Join(Environment.NewLine, reportLines);
        var reportSaved = TryPersist(() => storage.WriteAllText(reportPath, report, new UTF8Encoding(false)));
        persistenceSucceeded &= reportSaved;
        if (reportSaved)
        {
            var reportEventSaved = TryPersist(() => eventWriter.Append("report_written", "accepted", from: run.State, to: run.State,
                outputReference: "report.md", elapsedMilliseconds: stopwatch.ElapsedMilliseconds,
                unknownInformation: isFake ? ["実モデル・使用量・費用・PR操作は取得していません。"] :
                    ["モデル名・実費・請求額・硬い予算上限・PR操作は未取得です。", "子Agentの完全な観測・停止保証は未検証です。"],
                role: "Orchestrator"));
            persistenceSucceeded &= reportEventSaved;
            if (!reportEventSaved)
            {
                failure = AppendFailure(failure, "報告保存イベント report_written の記録に失敗しました。");
                run.StopInMemoryAfterPersistenceFailure();
                var stateLineIndex = reportLines.FindIndex(line => line.StartsWith("- 状態: `", StringComparison.Ordinal));
                reportLines[stateLineIndex] = $"- 状態: `{run.State}`";
                report = string.Join(Environment.NewLine, reportLines);
                report = report + $"{Environment.NewLine}- 永続化状態: 報告保存イベント report_written の記録に失敗しました。{Environment.NewLine}";
                var reportUpdated = TryPersist(() => storage.WriteAllText(reportPath, report, new UTF8Encoding(false)));
                persistenceSucceeded &= reportUpdated;
                if (!reportUpdated)
                    failure = AppendFailure(failure, "report.md への永続化失敗状態の追記にも失敗しました。");
            }
        }
        else
        {
            failure = AppendFailure(failure, "報告 report.md の保存に失敗しました。");
            var failureEventSaved = TryPersist(() => eventWriter.Append("report_write_failed", "needs_human_input", from: run.State, to: run.State,
                detail: "報告 report.md の保存に失敗しました。", role: "Orchestrator"));
            persistenceSucceeded &= failureEventSaved;
            if (!failureEventSaved)
                failure = AppendFailure(failure, "報告保存失敗イベント report_write_failed の記録にも失敗しました。");
        }
        if (!persistenceSucceeded)
        {
            failure = AppendFailure(failure, "状態は人間判断待ちですが、実行記録が不完全です。");
            run.StopInMemoryAfterPersistenceFailure();
        }
        return new FakeWorkflowResult(run.State, currentArtifact, currentHash, implementationAttempt,
            reviewAttempt, revisionAttempts, retryAttempts, "report.md", failure, persistenceSucceeded);
    }

    private string SaveExchange(string root, string role, int attempt, string name, string content)
    {
        var relative = Path.Combine("exchanges", $"{role}-{attempt}", name);
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        storage.WriteAllText(path, content, new UTF8Encoding(false));
        return relative.Replace('\\', '/');
    }

    private void SaveExecutionEvidence(string root, string role, int attempt, AgentExecutionEvidence? evidence)
    {
        if (evidence is null) return;
        var relative = Path.Combine("exchanges", $"{role}-{attempt}", "execution-evidence.json");
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        storage.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(evidence,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
    }

    private static bool TryPersist(Action write)
    {
        try { write(); return true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string AppendFailure(string? current, string addition) => current is null ? addition : $"{current} {addition}";

    private static string Outcome(AgentResponse response) => response.TimedOut ? "timeout" : response.StopRequested ? "stop_requested" :
        !response.Succeeded ? "failed" : string.IsNullOrWhiteSpace(response.Content) ? "empty" : "completed";

    private static string AgentFailure(string role, AgentResponse response) => response.TimedOut
        ? $"{role}がタイムアウトしました。"
        : response.StopRequested ? $"{role}への利用者停止要求を受け付けました。"
        : !response.Succeeded ? $"{role}が失敗しました: {response.Content}"
        : $"{role}が空応答を返しました。";

    private static AgentResponse InvokeAgent(Func<AgentResponse> invoke)
    {
        try { return invoke(); }
        catch (Exception exception) { return new($"Agent実行境界で例外が発生しました ({exception.GetType().Name})。", Succeeded: false); }
    }

    private static bool StopWasRequested(CancellationToken token) => token.IsCancellationRequested;
}

internal sealed class PhysicalFakeWorkflowStorage : IFakeWorkflowStorage
{
    public void WriteAllText(string path, string content, Encoding encoding) => File.WriteAllText(path, content, encoding);
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
}

/// <summary>Fakeワークフローの終端状態と、追跡に必要な試行情報です。</summary>
/// <param name="State">実行の終端状態です。</param>
/// <param name="ArtifactReference">最後に作成した成果物参照です。</param>
/// <param name="ArtifactSha256">最後に固定した成果物ハッシュです。</param>
/// <param name="ImplementationCalls">Implementer呼び出し数です。</param>
/// <param name="ReviewCalls">Reviewer呼び出し数です。</param>
/// <param name="RevisionAttempts">実施した修正回数です。</param>
/// <param name="RetryAttempts">実施した再試行回数です。</param>
/// <param name="ReportReference">最終Markdown報告の参照です。</param>
/// <param name="Detail">停止・失敗・未取得情報の説明です。</param>
public sealed record FakeWorkflowResult(RunState State, string? ArtifactReference, string? ArtifactSha256,
    int ImplementationCalls, int ReviewCalls, int RevisionAttempts, int RetryAttempts,
    string ReportReference, string? Detail, bool PersistenceSucceeded = true);
