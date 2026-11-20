using Ateliers.Ai.Orchestrator.Core;
using Xunit;

namespace Ateliers.Ai.Orchestrator.UnitTests;

public sealed class WorkflowPhaseThreeGateTests
{
    private const string BaseSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HeadSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string DiffSha = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void Evaluate_accepts_test_only_changes_and_all_required_release_results_on_the_same_sha()
    {
        var result = WorkflowPhaseThreeGate.Evaluate(ValidEvidence());

        Assert.True(result.Passed);
        Assert.Empty(result.Reasons);
    }

    [Theory]
    [InlineData("src/Ateliers.Ai.Orchestrator.Core/RunEvent.cs")]
    [InlineData("tests/../src/Ateliers.Ai.Orchestrator.Core/RunEvent.cs")]
    [InlineData("tests/README.md")]
    [InlineData("tests/fixtures/sample.json")]
    [InlineData("tests/Ateliers.Ai.Orchestrator.UnitTests/Ateliers.Ai.Orchestrator.UnitTests.csproj")]
    [InlineData("/tests/Ateliers.Ai.Orchestrator.UnitTests/NewTests.cs")]
    [InlineData("tests\\Ateliers.Ai.Orchestrator.UnitTests\\NewTests.cs")]
    public void Evaluate_rejects_paths_outside_the_test_tree_or_noncanonical_paths(string path)
    {
        var result = WorkflowPhaseThreeGate.Evaluate(ValidEvidence() with { ChangedPaths = [path] });

        Assert.False(result.Passed);
        Assert.Contains(result.Reasons, reason => reason.Contains("変更パス", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_rejects_missing_build_or_test_results_and_results_from_a_different_sha()
    {
        var evidence = ValidEvidence() with
        {
            ReleaseBuild = new WorkflowReleaseBuildEvidence(BaseSha, "Debug", 0),
            TestSuites = new Dictionary<WorkflowRequiredTestSuite, WorkflowTestSuiteEvidence>
            {
                [WorkflowRequiredTestSuite.OrchestratorUnitTests] = new(HeadSha, 0, 0, 0, 0)
            }
        };

        var result = WorkflowPhaseThreeGate.Evaluate(evidence);

        Assert.False(result.Passed);
        Assert.Contains(result.Reasons, reason => reason.Contains("Release build", StringComparison.Ordinal));
        Assert.Contains(result.Reasons, reason => reason.Contains("OrchestratorUnitTests", StringComparison.Ordinal));
        Assert.Contains(result.Reasons, reason => reason.Contains("SlackFakeTests", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_rejects_failed_or_unreported_test_counts()
    {
        var suites = ValidSuites().ToDictionary(pair => pair.Key, pair => pair.Value);
        suites[WorkflowRequiredTestSuite.SlackUnitTests] = new(HeadSha, 1, 12, 1, 0);
        suites[WorkflowRequiredTestSuite.SlackFakeTests] = new(HeadSha, 0, 12, null, 0);

        var result = WorkflowPhaseThreeGate.Evaluate(ValidEvidence() with { TestSuites = suites });

        Assert.False(result.Passed);
        Assert.Equal(2, result.Reasons.Count(reason => reason.Contains("Slack", StringComparison.Ordinal)));
    }

    [Fact]
    public void Evaluate_rejects_non_phase_three_evidence_and_missing_identifiers()
    {
        var result = WorkflowPhaseThreeGate.Evaluate(ValidEvidence() with
        {
            RunId = " ",
            Phase = WorkflowPhase.Phase1Implementation,
            DiffSha256 = "not-a-digest"
        });

        Assert.False(result.Passed);
        Assert.Contains(result.Reasons, reason => reason.Contains("Run ID", StringComparison.Ordinal));
        Assert.Contains(result.Reasons, reason => reason.Contains("Phase 3", StringComparison.Ordinal));
        Assert.Contains(result.Reasons, reason => reason.Contains("SHA-256", StringComparison.Ordinal));
    }

    private static WorkflowPhaseThreeEvidence ValidEvidence() => new(
        "run-123",
        WorkflowPhase.Phase3TestChanges,
        BaseSha,
        HeadSha,
        DiffSha,
        ["tests/Ateliers.Ai.Orchestrator.UnitTests/WorkflowPhaseThreeGateTests.cs"],
        new WorkflowReleaseBuildEvidence(HeadSha, "Release", 0),
        ValidSuites());

    private static IReadOnlyDictionary<WorkflowRequiredTestSuite, WorkflowTestSuiteEvidence> ValidSuites() =>
        Enum.GetValues<WorkflowRequiredTestSuite>().ToDictionary(
            suite => suite,
            _ => new WorkflowTestSuiteEvidence(HeadSha, 0, 12, 0, 0));
}

