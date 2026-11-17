using System.Text.Json;
using Xunit;

namespace Ateliers.Ai.Orchestrator.UnitTests;

public sealed class McpCliAdapterTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Adapter_passes_contract_to_cli_stdin_and_returns_cli_json()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "ateliers-mcp-cli-test-" + Guid.NewGuid().ToString("N"));
        var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            var bin = Path.Combine(root, "Ateliers-Dev", "Ai-Orchestrator", "bin");
            Directory.CreateDirectory(bin);
            var cliOutput = Path.GetDirectoryName(typeof(OrchestratorCli).Assembly.Location)!;
            foreach (var pattern in new[] { "Ateliers.Ai.Orchestrator.Cli.*", "Ateliers.Ai.Orchestrator.Core.*", "Ateliers.Slack.*" })
                foreach (var source in Directory.GetFiles(cliOutput, pattern))
                    File.Copy(source, Path.Combine(bin, Path.GetFileName(source)), overwrite: true);
            await File.WriteAllTextAsync(Path.Combine(bin, "install.json"), JsonSerializer.Serialize(new
            {
                version = OrchestratorCli.GetVersion().Split('+', 2)[0],
                runtimeIdentifier = "win-x64"
            }));
            Environment.SetEnvironmentVariable("LOCALAPPDATA", root);

            var workingDirectory = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workingDirectory);
            var runs = Path.Combine(root, "runs");
            using var document = JsonDocument.Parse(CreateContract(workingDirectory));
            var response = await new CliExecutionService().RunAsync(document.RootElement, "fake", null, runs, CancellationToken.None);

            using var result = JsonDocument.Parse(response);
            Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(0, result.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal("NeedsHumanInput", result.RootElement.GetProperty("state").GetString());
            Assert.True(Directory.Exists(result.RootElement.GetProperty("runDirectory").GetString()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateContract(string workingDirectory) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        taskId = "mcp-test",
        purpose = "MCP adapter test",
        workingDirectory,
        scope = new { included = new[] { "README.md" }, excluded = Array.Empty<string>() },
        inputs = Array.Empty<object>(),
        requirements = new[] { "Do not modify files." },
        acceptanceCriteria = new[] { "Return a recorded result." },
        delegatedOperations = Array.Empty<string>(),
        limits = new { maxDurationMinutes = 2, maxRevisionAttempts = 0, maxRetries = 0 },
        stopConditions = new[] { "Stop if the run fails." },
        reporting = new { pullRequestExpected = false, finalReportFormat = "markdown" }
    });
}
