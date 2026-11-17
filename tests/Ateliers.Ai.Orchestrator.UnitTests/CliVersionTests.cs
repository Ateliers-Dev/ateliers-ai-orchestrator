using Xunit;

namespace Ateliers.Ai.Orchestrator.UnitTests;

public sealed class CliVersionTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Version_command_reports_the_embedded_version()
    {
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var exitCode = await OrchestratorCli.RunAsync(["--version"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(OrchestratorCli.GetVersion(), output.ToString().Trim());
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }
}
