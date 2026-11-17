using System.Text.Json;
using Xunit;

namespace Ateliers.Ai.Orchestrator.UnitTests;

public sealed class StdioJsonCliTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Run_stdin_json_writes_a_single_structured_success_response()
    {
        var root = Path.Combine(Path.GetTempPath(), "ateliers-stdio-cli-test-" + Guid.NewGuid().ToString("N"));
        var workingDirectory = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workingDirectory);
        var runs = Path.Combine(root, "runs");
        var contract = CreateContract(workingDirectory);
        var originalIn = Console.In;
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var input = new StringReader(contract);
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetIn(input);
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await OrchestratorCli.RunAsync(["run", "--stdin-json", "--agent", "fake", "--runs", runs]);

            Assert.Equal(0, exitCode);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("NeedsHumanInput", json.RootElement.GetProperty("state").GetString());
            Assert.True(Directory.Exists(json.RootElement.GetProperty("runDirectory").GetString()));
            Assert.DoesNotContain("状態:", output.ToString());
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Run_stdin_json_reports_invalid_contract_as_json_and_exit_two()
    {
        var originalIn = Console.In;
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var input = new StringReader("{\"schemaVersion\":99}");
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetIn(input);
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await OrchestratorCli.RunAsync(["run", "--stdin-json", "--agent", "fake"]);

            Assert.Equal(2, exitCode);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("invalid_contract", json.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Contains("契約エラー:", error.ToString());
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static string CreateContract(string workingDirectory)
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "task-contract.json"));
        using var document = JsonDocument.Parse(source);
        var node = System.Text.Json.Nodes.JsonNode.Parse(source)!;
        node["workingDirectory"] = workingDirectory;
        node["reporting"]!["pullRequestExpected"] = false;
        return node.ToJsonString();
    }
}
