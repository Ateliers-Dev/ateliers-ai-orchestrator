using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Ateliers.Ai.Orchestrator.UnitTests;

public sealed class ExistingUnitTests
{
    [Theory]
    [MemberData(nameof(UnitTestCatalog.UnitTestCases), MemberType = typeof(UnitTestCatalog))]
    [Trait("Category", "Unit")]
    public void Existing_fake_and_unit_test_runs(string name) => UnitTestCatalog.RunUnitTest(name);

    [UnixFact]
    [Trait("Category", "Unit")]
    public void Non_pr_run_accepts_executable_mode_changes() =>
        UnitTestCatalog.RunUnixOnlyTest("PR不要のCodex実行では実行bit変更を許容する");

    [UnixFact]
    [Trait("Category", "Unit")]
    public void Pull_request_snapshot_preserves_new_executable_mode() =>
        UnitTestCatalog.RunUnixOnlyTest("PR必須Codex snapshotは新規実行ファイルのmodeを保持する");

    [Theory]
    [MemberData(nameof(UnitTestCatalog.GitIntegrationTestCases), MemberType = typeof(UnitTestCatalog))]
    [Trait("Category", "Integration")]
    public void Existing_local_git_integration_test_runs(string name) => UnitTestCatalog.RunGitIntegrationTest(name);
}

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Unix mode付きファイルの検証はWindowsでは実行できません。";
    }
}
