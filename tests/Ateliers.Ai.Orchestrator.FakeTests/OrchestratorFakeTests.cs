using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Ateliers.Ai.Orchestrator.FakeTests;

public sealed class OrchestratorFakeTests
{
    [Theory]
    [MemberData(nameof(OrchestratorTestCatalog.FakeTestCases), MemberType = typeof(OrchestratorTestCatalog))]
    [Trait("Category", "Fake")]
    public void Orchestrator_fake_cases_pass(string name) => OrchestratorTestCatalog.RunFakeTest(name);

    [UnixFact]
    [Trait("Category", "Fake")]
    public void Non_pr_run_accepts_executable_mode_changes() =>
        OrchestratorTestCatalog.RunUnixOnlyTest("PR不要のCodex実行では実行bit変更を許容する");

    [UnixFact]
    [Trait("Category", "Fake")]
    public void Pull_request_snapshot_preserves_new_executable_mode() =>
        OrchestratorTestCatalog.RunUnixOnlyTest("PR必須Codex snapshotは新規実行ファイルのmodeを保持する");

}

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Unix mode付きファイルの検証はWindowsでは実行できません。";
    }
}
