using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Ateliers.Ai.Orchestrator.UnitTests;

public sealed class McpCliAdapterTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Adapter_requires_the_requested_capability_to_be_true_for_the_installed_cli_version()
    {
        using var missing = JsonDocument.Parse("""{"cliVersion":"1.2.3","protocolVersion":1,"commands":{}}""");
        using var unsupported = JsonDocument.Parse("""{"cliVersion":"1.2.3","protocolVersion":1,"commands":{"contractSchema":false,"orchestrationGuide":false}}""");
        using var supported = JsonDocument.Parse("""{"cliVersion":"v1.2.3+build","protocolVersion":1,"commands":{"contractSchema":true,"orchestrationGuide":true}}""");

        foreach (var capability in new[] { "contractSchema", "orchestrationGuide" })
        {
            Assert.False(CliExecutionService.HasCapability(missing.RootElement, "1.2.3", capability));
            Assert.False(CliExecutionService.HasCapability(unsupported.RootElement, "1.2.3", capability));
            Assert.True(CliExecutionService.HasCapability(supported.RootElement, "1.2.3", capability));
            Assert.False(CliExecutionService.HasCapability(supported.RootElement, "1.2.4", capability));
        }
    }

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
        var originalCliPath = Environment.GetEnvironmentVariable("ATELIERS_AI_ORCHESTRATOR_CLI_PATH");
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
            }), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(root, "decoy-localappdata"));
            Environment.SetEnvironmentVariable("ATELIERS_AI_ORCHESTRATOR_CLI_PATH", Path.Combine(bin, "Ateliers.Ai.Orchestrator.Cli.exe"));

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
            Environment.SetEnvironmentVariable("ATELIERS_AI_ORCHESTRATOR_CLI_PATH", originalCliPath);
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Adapter_forwards_contract_schema_and_guide_from_installed_cli()
    {
        Adapter_requires_the_requested_capability_to_be_true_for_the_installed_cli_version();
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "ateliers-mcp-resource-test-" + Guid.NewGuid().ToString("N"));
        var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var originalCliPath = Environment.GetEnvironmentVariable("ATELIERS_AI_ORCHESTRATOR_CLI_PATH");
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
            }), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(root, "decoy-localappdata"));
            Environment.SetEnvironmentVariable("ATELIERS_AI_ORCHESTRATOR_CLI_PATH", Path.Combine(bin, "Ateliers.Ai.Orchestrator.Cli.exe"));

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
            Environment.SetEnvironmentVariable("ATELIERS_AI_ORCHESTRATOR_CLI_PATH", originalCliPath);
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

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Adapter_reports_an_invalid_explicit_cli_path_without_falling_back_to_localappdata()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "ateliers-mcp-invalid-cli-path-test-" + Guid.NewGuid().ToString("N"));
        var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var originalCliPath = Environment.GetEnvironmentVariable("ATELIERS_AI_ORCHESTRATOR_CLI_PATH");
        try
        {
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", root);
            Environment.SetEnvironmentVariable("ATELIERS_AI_ORCHESTRATOR_CLI_PATH", "relative\\Ateliers.Ai.Orchestrator.Cli.exe");

            var response = await new CliExecutionService().GetCliResourceAsync("--contract-schema", "contractSchema", CancellationToken.None);
            using var error = JsonDocument.Parse(response);
            Assert.Equal("cli_not_installed", error.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Contains("ATELIERS_AI_ORCHESTRATOR_CLI_PATH", error.RootElement.GetProperty("error").GetProperty("message").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
            Environment.SetEnvironmentVariable("ATELIERS_AI_ORCHESTRATOR_CLI_PATH", originalCliPath);
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Runtime_diagnostics_report_fixed_paths_and_manifest_values_without_starting_the_cli()
    {
        var root = Path.Combine(Path.GetTempPath(), "ateliers-mcp-diagnostics-test-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "Ateliers-Dev", "Ai-Orchestrator", "bin");
        Directory.CreateDirectory(bin);
        var cliPath = Path.Combine(bin, "Ateliers.Ai.Orchestrator.Cli.exe");
        var manifestPath = Path.Combine(bin, "install.json");
        try
        {
            await File.WriteAllTextAsync(cliPath, "fixture executable placeholder");
            await File.WriteAllTextAsync(manifestPath, "{\"version\":\"0.0.2\",\"runtimeIdentifier\":\"win-x64\"}");

            var response = CliExecutionService.CreateRuntimeDiagnostics(root, null, "C:\\fixture\\mcp.exe", "1.2.3.4", "1.2.3.0", "1.2.3+fixture");
            using var diagnostics = JsonDocument.Parse(response);
            var mcp = diagnostics.RootElement.GetProperty("mcp");
            var installation = diagnostics.RootElement.GetProperty("installation");

            Assert.Equal("C:\\fixture\\mcp.exe", mcp.GetProperty("processPath").GetString());
            Assert.Equal("1.2.3.4", mcp.GetProperty("processFileVersion").GetString());
            Assert.Equal(cliPath, installation.GetProperty("cliPath").GetString());
            Assert.True(installation.GetProperty("cliExists").GetBoolean());
            Assert.Equal(manifestPath, installation.GetProperty("manifestPath").GetString());
            Assert.Equal("valid", installation.GetProperty("manifestStatus").GetString());
            Assert.Equal("0.0.2", installation.GetProperty("manifestVersion").GetString());
            Assert.Equal("win-x64", installation.GetProperty("runtimeIdentifier").GetString());
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Runtime_diagnostics_report_the_explicit_cli_path_and_accept_a_utf8_bom_manifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "ateliers-mcp-explicit-diagnostics-test-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "custom", "cli");
        Directory.CreateDirectory(bin);
        var cliPath = Path.Combine(bin, "Ateliers.Ai.Orchestrator.Cli.exe");
        try
        {
            await File.WriteAllTextAsync(cliPath, "fixture executable placeholder");
            await File.WriteAllTextAsync(Path.Combine(bin, "install.json"), "{\"version\":\"0.0.2\",\"runtimeIdentifier\":\"win-x64\"}", new System.Text.UTF8Encoding(true));

            var response = CliExecutionService.CreateRuntimeDiagnostics(
                Path.Combine(root, "decoy-localappdata"), cliPath, "C:\\fixture\\mcp.exe", "1.2.3.4", "1.2.3.0", "1.2.3+fixture");
            using var diagnostics = JsonDocument.Parse(response);
            var installation = diagnostics.RootElement.GetProperty("installation");

            Assert.Equal("environment", installation.GetProperty("cliPathSource").GetString());
            Assert.Equal(cliPath, installation.GetProperty("configuredCliPath").GetString());
            Assert.Equal(cliPath, installation.GetProperty("cliPath").GetString());
            Assert.Equal("valid", installation.GetProperty("manifestStatus").GetString());
            Assert.Equal("0.0.2", installation.GetProperty("manifestVersion").GetString());
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Runtime_diagnostics_identify_a_non_absolute_explicit_cli_path()
    {
        var response = CliExecutionService.CreateRuntimeDiagnostics("C:\\fixture", "relative\\cli.exe", null, null, null, null);
        using var diagnostics = JsonDocument.Parse(response);
        var installation = diagnostics.RootElement.GetProperty("installation");

        Assert.Equal("environment", installation.GetProperty("cliPathSource").GetString());
        Assert.Equal("configured_path_must_be_absolute", installation.GetProperty("cliPathError").GetString());
        Assert.Equal(JsonValueKind.Null, installation.GetProperty("cliPath").ValueKind);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Runtime_diagnostics_report_json_parse_location_without_manifest_contents()
    {
        var root = Path.Combine(Path.GetTempPath(), "ateliers-mcp-diagnostics-json-test-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "Ateliers-Dev", "Ai-Orchestrator", "bin");
        Directory.CreateDirectory(bin);
        var manifestPath = Path.Combine(bin, "install.json");
        try
        {
            await File.WriteAllTextAsync(manifestPath, "{\"private\":\"SECRET_MARKER\",\nBROKEN}");

            var response = CliExecutionService.CreateRuntimeDiagnostics(root, null, null, null, "1.2.3.0", "1.2.3+fixture");
            using var diagnostics = JsonDocument.Parse(response);
            var installation = diagnostics.RootElement.GetProperty("installation");
            var parseError = installation.GetProperty("parseError");

            Assert.Equal("invalid_json", installation.GetProperty("manifestStatus").GetString());
            Assert.Equal("JsonReaderException", parseError.GetProperty("type").GetString());
            Assert.True(parseError.GetProperty("lineNumber").GetInt64() >= 0);
            Assert.False(response.Contains("SECRET_MARKER", StringComparison.Ordinal));
            Assert.False(parseError.ToString().Contains(manifestPath, StringComparison.Ordinal));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Runtime_diagnostics_does_not_parse_an_oversized_manifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "ateliers-mcp-diagnostics-large-test-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "Ateliers-Dev", "Ai-Orchestrator", "bin");
        Directory.CreateDirectory(bin);
        var manifestPath = Path.Combine(bin, "install.json");
        try
        {
            await File.WriteAllTextAsync(manifestPath, new string('x', 16 * 1024 + 1));

            var response = CliExecutionService.CreateRuntimeDiagnostics(root, null, null, null, "1.2.3.0", "1.2.3+fixture");
            using var diagnostics = JsonDocument.Parse(response);
            var installation = diagnostics.RootElement.GetProperty("installation");

            Assert.Equal("too_large", installation.GetProperty("manifestStatus").GetString());
            Assert.Equal(16 * 1024 + 1, installation.GetProperty("manifestBytes").GetInt64());
            Assert.Equal(JsonValueKind.Null, installation.GetProperty("parseError").ValueKind);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
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
