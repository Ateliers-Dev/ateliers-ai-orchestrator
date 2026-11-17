using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Ateliers.Ai.Orchestrator.UnitTests;

public sealed class McpCliAdapterTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Adapter_rejects_multibyte_cli_output_over_64_KiB()
    {
        const int maxBytes = 64 * 1024;
        var oversizedJson = "{\"message\":\"" + new string('あ', maxBytes / 3 + 1) + "\"}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(oversizedJson);
        Assert.True(oversizedJson.Length < maxBytes, "Fixture must fit the old character limit.");
        Assert.True(bytes.Length > maxBytes, "Fixture must exceed the protocol byte limit.");

        await using var stream = new MemoryStream(bytes);
        var captured = await CliExecutionService.ReadBoundedAsync(stream, maxBytes);
        using var response = JsonDocument.Parse(CliExecutionService.CreateCliResponse(captured, 0));

        Assert.True(captured.Truncated);
        Assert.Equal("cli_response_too_large", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Adapter_starts_npm_cmd_codex_shim_and_returns_structured_result_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.True(Environment.Is64BitOperatingSystem, "MCP adapter requires Windows x64 for this fixture.");

        var root = Path.Combine(Path.GetTempPath(), "ateliers-mcp-codex-cmd-test-" + Guid.NewGuid().ToString("N"));
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
            RunGit(workingDirectory, "init", "--quiet");
            RunGit(workingDirectory, "config", "user.name", "MCP fixture");
            RunGit(workingDirectory, "config", "user.email", "mcp-fixture@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "README.md"), "fixture\n");
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, ".gitignore"), ".ateliers-orchestrator/\n");
            RunGit(workingDirectory, "add", "README.md", ".gitignore");
            RunGit(workingDirectory, "commit", "--quiet", "-m", "fixture");

            var marker = Path.Combine(root, "codex-shim-started.txt");
            var codexCmd = Path.Combine(root, "codex.cmd");
            await File.WriteAllTextAsync(codexCmd, $"@echo off{Environment.NewLine}echo started>\"{marker}\"{Environment.NewLine}exit /b 2{Environment.NewLine}");
            using var document = JsonDocument.Parse(CreateContract(workingDirectory));
            var runsDirectory = Path.Combine(root, "runs");
            var response = await new CliExecutionService().RunAsync(document.RootElement, "codex", codexCmd, runsDirectory, CancellationToken.None);

            using var result = JsonDocument.Parse(response);
            var runDirectory = result.RootElement.TryGetProperty("runDirectory", out var runPath) ? runPath.GetString() : null;
            var evidence = runDirectory is not null && Directory.Exists(runDirectory)
                ? string.Join(Environment.NewLine, Directory.EnumerateFiles(runDirectory, "*", SearchOption.AllDirectories)
                    .Select(file => $"{Path.GetRelativePath(runDirectory, file)}: {File.ReadAllText(file)}"))
                : "run evidence unavailable";
            Assert.True(File.Exists(marker), $"run_orchestration did not start the configured npm .cmd shim. Response: {response}; evidence: {evidence}");
            Assert.True(result.RootElement.TryGetProperty("state", out _), "CLI result was not returned as structured JSON.");
            Assert.True(result.RootElement.TryGetProperty("exitCode", out _), "CLI exit status was not returned.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
            DeleteTemporaryDirectory(root);
        }
    }

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
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Adapter_forwards_contract_schema_and_guide_from_installed_cli()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "ateliers-mcp-resource-test-" + Guid.NewGuid().ToString("N"));
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

            var service = new CliExecutionService();
            var schemaText = await service.GetCliResourceAsync("--contract-schema", "contractSchema", CancellationToken.None);
            using var schema = JsonDocument.Parse(schemaText);
            Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
            var guide = await service.GetCliResourceAsync("--orchestration-guide", "orchestrationGuide", CancellationToken.None);
            Assert.True(guide.Contains("workingDirectory", StringComparison.Ordinal), guide);
            Assert.DoesNotContain("\"error\"", guide);
            var cancelled = await service.GetCliResourceAsync("--contract-schema", "contractSchema", new CancellationToken(canceled: true));
            using var cancellationError = JsonDocument.Parse(cancelled);
            Assert.Equal("cli_cancelled", cancellationError.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Adapter_returns_structured_error_when_cli_is_missing()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "ateliers-mcp-no-cli-test-" + Guid.NewGuid().ToString("N"));
        var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", root);
            var response = await new CliExecutionService().GetCliResourceAsync("--contract-schema", "contractSchema", CancellationToken.None);
            using var error = JsonDocument.Parse(response);
            Assert.Equal("cli_not_installed", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
            Directory.Delete(root, recursive: true);
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

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git fixture failed to start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {stdout} {stderr}");
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(entry, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
