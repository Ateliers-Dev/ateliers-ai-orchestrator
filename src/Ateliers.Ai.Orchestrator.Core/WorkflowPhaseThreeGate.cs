namespace Ateliers.Ai.Orchestrator.Core;

/// <summary>Phase 3で必須とする決定論的テスト群です。</summary>
public enum WorkflowRequiredTestSuite
{
    /// <summary>OrchestratorのUnitTestsです。</summary>
    OrchestratorUnitTests,
    /// <summary>OrchestratorのFakeTestsです。</summary>
    OrchestratorFakeTests,
    /// <summary>SlackのUnitTestsです。</summary>
    SlackUnitTests,
    /// <summary>SlackのFakeTestsです。</summary>
    SlackFakeTests
}

/// <summary>Phase 3で取得したRelease buildの結果です。</summary>
public sealed record WorkflowReleaseBuildEvidence(string CommitSha, string Configuration, int? ExitCode);

/// <summary>必須テスト群ひとつの実行結果です。</summary>
public sealed record WorkflowTestSuiteEvidence(
    string CommitSha,
    int? ExitCode,
    int? PassedCount,
    int? FailedCount,
    int? SkippedCount);

/// <summary>Phase 3の変更範囲と品質確認に使う取得済み証跡です。</summary>
public sealed record WorkflowPhaseThreeEvidence(
    string RunId,
    WorkflowPhase Phase,
    string BaseCommitSha,
    string HeadCommitSha,
    string DiffSha256,
    IReadOnlyCollection<string> ChangedPaths,
    WorkflowReleaseBuildEvidence? ReleaseBuild,
    IReadOnlyDictionary<WorkflowRequiredTestSuite, WorkflowTestSuiteEvidence>? TestSuites);

/// <summary>Phase 3ゲートの判定結果です。</summary>
public sealed record WorkflowPhaseGateResult(bool Passed, IReadOnlyList<string> Reasons);

/// <summary>Phase 3の変更範囲とRelease build・必須テスト証跡を検証します。</summary>
public static class WorkflowPhaseThreeGate
{
    private static readonly WorkflowRequiredTestSuite[] RequiredSuites =
    [
        WorkflowRequiredTestSuite.OrchestratorUnitTests,
        WorkflowRequiredTestSuite.OrchestratorFakeTests,
        WorkflowRequiredTestSuite.SlackUnitTests,
        WorkflowRequiredTestSuite.SlackFakeTests
    ];

    /// <summary>証跡がすべてそろい、同一対象SHAに対するPhase 3の必須条件を満たすか判定します。</summary>
    public static WorkflowPhaseGateResult Evaluate(WorkflowPhaseThreeEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var reasons = new List<string>();

        if (string.IsNullOrWhiteSpace(evidence.RunId))
            reasons.Add("Run IDがありません。");
        if (evidence.Phase != WorkflowPhase.Phase3TestChanges)
            reasons.Add("Phase 3以外の証跡はこのゲートで受け付けません。");
        if (!IsSha(evidence.BaseCommitSha) || !IsSha(evidence.HeadCommitSha) ||
            string.Equals(evidence.BaseCommitSha, evidence.HeadCommitSha, StringComparison.OrdinalIgnoreCase))
            reasons.Add("有効な開始SHAと異なる終了SHAが必要です。");
        if (!IsSha(evidence.DiffSha256))
            reasons.Add("変更差分のSHA-256がありません。");

        if (evidence.ChangedPaths is null || evidence.ChangedPaths.Count == 0)
        {
            reasons.Add("変更ファイル一覧がありません。");
        }
        else
        {
            foreach (var path in evidence.ChangedPaths)
            {
                if (!IsTestPath(path))
                {
                    reasons.Add($"Phase 3で許可されない変更パスです: {path}");
                }
            }
        }

        var build = evidence.ReleaseBuild;
        if (build is null || !string.Equals(build.Configuration, "Release", StringComparison.Ordinal) ||
            build.ExitCode != 0 || !string.Equals(build.CommitSha, evidence.HeadCommitSha, StringComparison.OrdinalIgnoreCase))
            reasons.Add("Phase 3終了時の同一対象SHAに対するRelease build成功証跡がありません。");

        var suites = evidence.TestSuites;
        foreach (var suite in RequiredSuites)
        {
            if (suites is null || !suites.TryGetValue(suite, out var result) ||
                !string.Equals(result.CommitSha, evidence.HeadCommitSha, StringComparison.OrdinalIgnoreCase) ||
                result.ExitCode != 0 || result.PassedCount is null or <= 0 ||
                result.FailedCount is not 0 || result.SkippedCount is null or < 0)
            {
                reasons.Add($"{suite}の同一対象SHAに対する成功証跡がありません。");
            }
        }

        return new WorkflowPhaseGateResult(reasons.Count == 0, reasons.AsReadOnly());
    }

    private static bool IsTestPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !path.Contains('\\') &&
        !path.StartsWith("/", StringComparison.Ordinal) &&
        !path.Split('/').Any(segment => segment is "" or "." or "..") &&
        path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase);

    private static bool IsSha(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);
}

