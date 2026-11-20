using System.Text.Json;
using Ateliers.Ai.Orchestrator.Core;
using Xunit;

namespace Ateliers.Ai.Orchestrator.FakeTests;

public sealed class StdioJsonCliTests
{
    [Fact]
    [Trait("Category", "Fake")]
    public async Task Cli_publishes_contract_schema_guide_and_capability_flags()
    {
        var originalOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, await OrchestratorCli.RunAsync(["--contract-schema"]));
            using var schema = JsonDocument.Parse(output.ToString());
            var root = schema.RootElement;
            Assert.Equal("object", root.GetProperty("type").GetString());
            Assert.False(root.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(1, root.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32());
            Assert.Contains("taskId", root.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", root.GetProperty("properties").GetProperty("taskId").GetProperty("pattern").GetString());
            Assert.NotNull(root.GetProperty("properties").GetProperty("workingDirectory").GetProperty("pattern").GetString());
            Assert.Equal(1, root.GetProperty("$defs").GetProperty("executionLimits").GetProperty("properties").GetProperty("maxDurationMinutes").GetProperty("minimum").GetInt32());
            Assert.Equal(int.MaxValue, root.GetProperty("$defs").GetProperty("executionLimits").GetProperty("properties").GetProperty("maxDurationMinutes").GetProperty("maximum").GetInt32());
            var nonBlankPattern = root.GetProperty("$defs").GetProperty("nonBlankString").GetProperty("pattern").GetString()!;
            Assert.Equal("^[\\s\\S]*\\S[\\s\\S]*$", nonBlankPattern);
            Assert.Equal(nonBlankPattern, root.GetProperty("properties").GetProperty("purpose").GetProperty("pattern").GetString());
            Assert.Matches(nonBlankPattern, "first line\nsecond line");
            Assert.DoesNotMatch(nonBlankPattern, " \r\n\t ");
            var multilineContract = System.Text.Json.Nodes.JsonNode.Parse(ValidExampleContract())!;
            multilineContract["purpose"] = "first line\nsecond line";
            multilineContract["requirements"]![0] = "first line\nsecond line";
            Assert.NotNull(TaskContractReader.ReadJson(multilineContract.ToJsonString()).Contract);
            multilineContract["requirements"]![0] = " \r\n\t ";
            Assert.Null(TaskContractReader.ReadJson(multilineContract.ToJsonString()).Contract);
            AssertSchemaMatches(typeof(TaskContract), root);
            AssertSchemaMatches(typeof(TaskScope), root.GetProperty("$defs").GetProperty("taskScope"));
            AssertSchemaMatches(typeof(TaskInput), root.GetProperty("$defs").GetProperty("taskInput"));
            AssertSchemaMatches(typeof(ExecutionLimits), root.GetProperty("$defs").GetProperty("executionLimits"));
            AssertSchemaMatches(typeof(ReportingRequirements), root.GetProperty("$defs").GetProperty("reportingRequirements"));
            Assert.Equal("markdown", TaskContractReader.ReadJson(ValidExampleContract()).Contract?.Reporting.FinalReportFormat);

            output.GetStringBuilder().Clear();
            Assert.Equal(0, await OrchestratorCli.RunAsync(["--orchestration-guide"]));
            Assert.Contains("workingDirectory", output.ToString());
            Assert.Equal(OrchestrationGuide.GetText(), output.ToString());

            output.GetStringBuilder().Clear();
            Assert.Equal(0, await OrchestratorCli.RunAsync(["--capabilities"]));
            using var capabilities = JsonDocument.Parse(output.ToString());
            var commands = capabilities.RootElement.GetProperty("commands");
            Assert.True(commands.GetProperty("runStdinJson").GetBoolean());
            Assert.True(commands.GetProperty("contractSchema").GetBoolean());
            Assert.True(commands.GetProperty("orchestrationGuide").GetBoolean());
        }
        finally { Console.SetOut(originalOut); }
    }

    [Fact]
    [Trait("Category", "Fake")]
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
            Assert.Equal(0, json.RootElement.GetProperty("slack").GetProperty("sent").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("slack").GetProperty("failed").GetInt32());
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
    [Trait("Category", "Fake")]
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

    private static string ValidExampleContract()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "task-contract.json"));
        var node = System.Text.Json.Nodes.JsonNode.Parse(source)!;
        node["workingDirectory"] = Directory.GetCurrentDirectory();
        return node.ToJsonString();
    }

    private static void AssertSchemaMatches(Type contractType, JsonElement schema)
    {
        var names = contractType.GetProperties()
            .Select(property => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var schemaNames = schema.GetProperty("properties").EnumerateObject().Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(names, schemaNames);
        Assert.Equal(names, schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }
}
