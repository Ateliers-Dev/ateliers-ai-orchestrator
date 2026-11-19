using Xunit;

namespace Ateliers.Ai.Orchestrator.IntegrationTests;

public sealed class LocalGitIntegrationTests
{
    [Theory]
    [MemberData(nameof(OrchestratorTestCatalog.GitIntegrationTestCases), MemberType = typeof(OrchestratorTestCatalog))]
    [Trait("Category", "Integration")]
    public void Orchestrator_integrates_with_a_real_local_git_cli(string name) =>
        OrchestratorTestCatalog.RunGitIntegrationTest(name);
}
