using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Sdk;
using Ateliers.Ai.Orchestrator.TestFixtures;

namespace Ateliers.Ai.Orchestrator.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class CodexIntegrationTests
{
    private const string OptInVariable = "ATELIERS_RUN_CODEX_INTEGRATION_TESTS";
    private const string CodexPathVariable = "ATELIERS_CODEX_CLI_PATH";

    [CodexOptInFact]
    [Trait("Category", "ExternalIntegration")]
    public async Task RunAsync_ShouldConnectToCodexAndReceiveResponse()
    {
        var fixture = new IsolatedFixture();
        var succeeded = false;
        ProcessResult? run = null;
        string? runDirectory = null;

        try
        {
            var codexExecutable = Environment.GetEnvironmentVariable(CodexPathVariable);
            Assert.False(string.IsNullOrWhiteSpace(codexExecutable),
                $"{CodexPathVariable} に認証済みCodex CLIの絶対パスを設定してください。");
            Assert.True(Path.IsPathFullyQualified(codexExecutable), $"{CodexPathVariable} は絶対パスで指定してください。");
            Assert.True(File.Exists(codexExecutable), $"Codex CLIが見つかりません: {codexExecutable}");

            fixture.CreateSeedRepository();
            fixture.WriteTaskContract();
            var cliAssembly = FindCliAssembly();
            runDirectory = fixture.RunsDirectory;
            run = await InvokeOrchestratorAsync(cliAssembly, Path.GetFullPath(codexExecutable!), fixture);
            Assert.True(run.ExitCode == 0,
                $"Orchestrator終了コードが0ではありません: {run.ExitCode}");
            var runDirectoryLine = run.StandardOutput.Split(Environment.NewLine)
                .FirstOrDefault(line => line.StartsWith("記録先: ", StringComparison.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(runDirectoryLine),
                $"Orchestrator出力に実行記録先がありません。stdout:{Environment.NewLine}{run.StandardOutput}");
            runDirectory = Path.GetFullPath(runDirectoryLine!["記録先: ".Length..].Trim());
            var runsRoot = Path.GetFullPath(fixture.RunsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            Assert.True(runDirectory.StartsWith(runsRoot,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
                $"実行記録先がfixtureのruns配下ではありません: {runDirectory}");

            var exchangeDirectory = Path.Combine(runDirectory, "exchanges");
            Assert.True(Directory.Exists(exchangeDirectory), $"Agent応答の保存先がありません: {exchangeDirectory}");
            var implementerResponses = Directory.EnumerateDirectories(exchangeDirectory, "implementer-*")
                .Select(directory => Path.Combine(directory, "response.md"))
                .Where(File.Exists)
                .ToArray();
            Assert.True(implementerResponses.Length > 0,
                $"Implementer応答ファイルが見つかりません: {exchangeDirectory}");
            Assert.Contains(implementerResponses,
                path => !string.IsNullOrWhiteSpace(File.ReadAllText(path)));
            succeeded = true;
        }
        catch (Exception exception) when (exception is not XunitException)
        {
            throw new XunitException(FormatFailure(exception, fixture.RootDirectory, runDirectory, run));
        }
        catch (XunitException exception)
        {
            throw new XunitException(FormatFailure(exception, fixture.RootDirectory, runDirectory, run));
        }
        finally
        {
            if (succeeded)
            {
                try
                {
                    fixture.Dispose();
                }
                catch (Exception cleanupException)
                {
                    throw new XunitException(
                        $"IntegrationTestのassertはすべて成功しましたが、fixture cleanupに失敗しました。" +
                        $"{Environment.NewLine}Fixture: {fixture.RootDirectory}" +
                        $"{Environment.NewLine}Cleanup exception:{Environment.NewLine}{cleanupException}");
                }
            }
            else
            {
                Console.Error.WriteLine($"失敗時の証跡を保全しました: {fixture.RootDirectory}");
            }
        }

    }

    private static string FormatFailure(Exception exception, string fixtureDirectory, string? runDirectory, ProcessResult? run)
    {
        var runEvidence = runDirectory is null ? "(runディレクトリ未作成または特定できません)" : runDirectory;
        return $"IntegrationTests失敗: {exception.Message}{Environment.NewLine}" +
            $"Fixture: {fixtureDirectory}{Environment.NewLine}Run evidence: {runEvidence}{Environment.NewLine}" +
            $"Orchestrator exit code: {run?.ExitCode.ToString() ?? "(未取得)"}{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{run?.StandardOutput ?? "(未取得)"}{Environment.NewLine}" +
            $"stderr:{Environment.NewLine}{run?.StandardError ?? "(未取得)"}";
    }

    private static string FindCliAssembly()
    {
        var copiedAssembly = Path.Combine(AppContext.BaseDirectory, "Ateliers.Ai.Orchestrator.Cli.dll");
        if (File.Exists(copiedAssembly)) return copiedAssembly;

        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var releaseProject = Path.Combine(current.FullName, "src", "Ateliers.Ai.Orchestrator.Cli", "bin", "Release", "net10.0", "Ateliers.Ai.Orchestrator.Cli.dll");
            if (File.Exists(releaseProject)) return releaseProject;
            var debugProject = Path.Combine(current.FullName, "src", "Ateliers.Ai.Orchestrator.Cli", "bin", "Debug", "net10.0", "Ateliers.Ai.Orchestrator.Cli.dll");
            if (File.Exists(debugProject)) return debugProject;
        }
        throw new FileNotFoundException("CLI assemblyが見つかりません。先にソリューションをビルドしてください。");
    }

    private static async Task<ProcessResult> InvokeOrchestratorAsync(string cliAssembly, string codexExecutable, IsolatedFixture fixture)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.RepositoryDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            cliAssembly, "run", fixture.ContractPath, "--agent", "codex", "--codex-exe", codexExecutable,
            "--runs", fixture.RunsDirectory, "--slack-notifications", "disabled"
        }) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Orchestrator CLIを開始できませんでした。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            throw new TimeoutException(
                $"Orchestratorが12分以内に終了しませんでした。終了コード: {process.ExitCode}{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{await stdout}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{await stderr}");
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
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
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Gitプロセスを開始できませんでした。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout;
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {arguments[0]} に失敗しました: {await stderr}");
        return output;
    }


    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);


}

public sealed class CodexOptInFactAttribute : FactAttribute
{
    public CodexOptInFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ATELIERS_RUN_CODEX_INTEGRATION_TESTS") != "1")
            Skip = "実Codex連携テストは既定でSkipされます。明示opt-in手順はREADMEを参照してください。";
    }
}
