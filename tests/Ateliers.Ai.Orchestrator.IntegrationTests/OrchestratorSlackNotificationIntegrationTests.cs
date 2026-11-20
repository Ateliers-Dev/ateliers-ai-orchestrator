using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ateliers.Slack;
using Xunit;
using Xunit.Sdk;

namespace Ateliers.Ai.Orchestrator.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class OrchestratorSlackNotificationIntegrationTests
{
    [OrchestratorSlackOptInFact]
    [Trait("Category", "ExternalIntegration")]
    public async Task Fake_stop_run_sends_lifecycle_notifications_to_the_test_channel()
    {
        var root = Path.Combine(Path.GetTempPath(), "ateliers-orchestrator-slack-integration-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var runs = Path.Combine(root, "runs");
        var lifecycleLog = Path.Combine(root, "lifecycle.jsonl");
        var succeeded = false;
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(runs);

        try
        {
            var repositoryRoot = FindRepositoryRoot();
            var (slackConfiguration, slackSecrets) = FindSlackSettings(repositoryRoot);
            var botRoutes = FindBotRoutes(repositoryRoot);

            using (var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(
                       slackConfiguration, slackSecrets, useTestChannel: true, botRoutes, out var warning))
            {
                Assert.True(dispatcher is not null, $"Slackテスト設定を利用できません: {warning ?? "設定を確認してください。"}");
                Assert.True(await dispatcher!.VerifyBotIdentitiesAsync(),
                    "Slack Bot認証と設定済みBot User IDが一致しないため、通知を送信しませんでした。");
            }

            var contractPath = Path.Combine(root, "task-contract.json");
            var contract = JsonNodeForContract(repositoryRoot, workspace);
            await File.WriteAllTextAsync(contractPath, contract);

            var run = await InvokeOrchestratorAsync(FindCliAssembly(), workspace, contractPath, runs, lifecycleLog,
                slackConfiguration, slackSecrets);
            Assert.True(run.ExitCode == 0,
                $"Orchestrator終了コードが0ではありません: {run.ExitCode}{Environment.NewLine}{run.StandardOutput}{Environment.NewLine}{run.StandardError}");
            Assert.Contains("状態: NeedsHumanInput", run.StandardOutput, StringComparison.Ordinal);

            var runIdMatch = Regex.Match(run.StandardOutput, @"Fake実行を記録しました: ([0-9a-f]{32})");
            Assert.True(runIdMatch.Success, "CLI出力からRun IDを特定できませんでした。");
            var runId = runIdMatch.Groups[1].Value;
            Assert.True(File.Exists(lifecycleLog), "Slack配送監査JSONLが作成されていません。");

            using var deliveries = JsonDocument.Parse("[" + string.Join(",", await File.ReadAllLinesAsync(lifecycleLog)) + "]");
            var sent = deliveries.RootElement.EnumerateArray()
                .Where(item => item.GetProperty("eventType").GetString() == "slack_delivery_result" &&
                               item.GetProperty("status").GetString() == "sent")
                .ToArray();
            Assert.True(sent.Length >= 5, $"Slack送信成功数が不足しています: {sent.Length}");
            Assert.All(sent, item => Assert.Equal(runId, item.GetProperty("runId").GetString()));
            Assert.Contains(sent, item => item.GetProperty("role").GetString() == "Implementer");
            Assert.Contains(sent, item => item.GetProperty("role").GetString() == "Reviewer");

            var runStart = Assert.Single(sent, item => item.GetProperty("role").GetString() == "Orchestrator" &&
                                                       item.GetProperty("relatedId").GetString() == "started");
            Assert.False(string.IsNullOrWhiteSpace(runStart.GetProperty("slackParentMessageTimestamp").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(runStart.GetProperty("slackRunIdReplyTimestamp").GetString()));
            Assert.DoesNotContain(deliveries.RootElement.EnumerateArray(), item =>
                item.GetProperty("eventType").GetString() == "slack_delivery_result" &&
                item.GetProperty("status").GetString() is "failed" or "abandoned" or "dropped");
            succeeded = true;
        }
        catch (Exception exception) when (exception is not XunitException)
        {
            throw new XunitException($"Slack Orchestrator IntegrationTestに失敗しました。Fixture: {root}{Environment.NewLine}{exception}");
        }
        catch (XunitException exception)
        {
            throw new XunitException($"Slack Orchestrator IntegrationTestに失敗しました。Fixture: {root}{Environment.NewLine}{exception.Message}");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                if (succeeded) Directory.Delete(root, recursive: true);
                else Console.Error.WriteLine($"失敗時のrun・配送証跡を保持しました: {root}");
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "examples", "task-contract.json")) &&
                File.Exists(Path.Combine(current.FullName, "config", "slack.json.example")))
                return current.FullName;
        throw new DirectoryNotFoundException("リポジトリルートを特定できませんでした。");
    }

    private static (string Configuration, string Secrets) FindSlackSettings(string repositoryRoot)
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var userConfig = string.IsNullOrWhiteSpace(localAppData) ? null :
            Path.Combine(localAppData, "Ateliers-Dev", "Ai-Orchestrator", "config");
        var candidates = new[] { userConfig, Path.Combine(repositoryRoot, "config") }.Where(path => path is not null).Cast<string>();
        foreach (var directory in candidates)
        {
            var configuration = Path.Combine(directory, "slack.json");
            var secrets = Path.Combine(directory, "slack.secrets.json");
            if (!File.Exists(configuration) && !File.Exists(secrets)) continue;
            Assert.True(File.Exists(configuration) && File.Exists(secrets),
                "Slack設定とsecretsは同じ設定フォルダーに両方必要です。");
            return (Path.GetFullPath(configuration), Path.GetFullPath(secrets));
        }
        throw new XunitException("Slack IntegrationTest用の設定がありません。ユーザー設定またはリポジトリconfigに設定してください。");
    }

    private static IReadOnlyDictionary<string, string> FindBotRoutes(string repositoryRoot)
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var userAgents = string.IsNullOrWhiteSpace(localAppData) ? null :
            Path.Combine(localAppData, "Ateliers-Dev", "Ai-Orchestrator", "config", "agents.json");
        var repositoryAgents = Path.Combine(repositoryRoot, "config", "agents.json");
        var path = userAgents is not null && File.Exists(userAgents) ? userAgents : repositoryAgents;
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Orchestrator"] = "orchestrator", ["Implementer"] = "implementer", ["Reviewer"] = "reviewer"
            };

        using var assignments = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, string>(StringComparer.Ordinal) { ["Orchestrator"] = "orchestrator" };
        foreach (var role in new[] { "Implementer", "Reviewer" })
        {
            var agentId = assignments.RootElement.GetProperty("assignments").GetProperty(role)[0].GetProperty("agentId").GetString()
                ?? throw new XunitException($"{role} Agent IDを設定から読み取れません。");
            var profilePath = Path.Combine(Path.GetDirectoryName(path)!, "agents", $"{role.ToLowerInvariant()}-{agentId}.json");
            using var profile = JsonDocument.Parse(File.ReadAllText(profilePath));
            result[role] = profile.RootElement.GetProperty("slackBotId").GetString()
                ?? throw new XunitException($"{role} Slack Bot IDを設定から読み取れません。");
        }
        return result;
    }

    private static string JsonNodeForContract(string repositoryRoot, string workspace)
    {
        var source = File.ReadAllText(Path.Combine(repositoryRoot, "examples", "task-contract.json"));
        var contract = System.Text.Json.Nodes.JsonNode.Parse(source)!;
        contract["taskId"] = "orchestrator-slack-stop-integration";
        contract["purpose"] = "Fake StopシナリオでSlackの人間向け通知とRun ID返信を確認する";
        contract["workingDirectory"] = workspace;
        contract["reporting"]!["pullRequestExpected"] = false;
        return contract.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string FindCliAssembly()
    {
        var copied = Path.Combine(AppContext.BaseDirectory, "Ateliers.Ai.Orchestrator.Cli.dll");
        if (File.Exists(copied)) return copied;
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            foreach (var configuration in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(current.FullName, "src", "Ateliers.Ai.Orchestrator.Cli", "bin", configuration,
                    "net10.0", "Ateliers.Ai.Orchestrator.Cli.dll");
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException("CLI assemblyが見つかりません。先にソリューションをビルドしてください。");
    }

    private static async Task<ProcessResult> InvokeOrchestratorAsync(string cliAssembly, string workingDirectory, string contractPath,
        string runs, string lifecycleLog, string slackConfiguration, string slackSecrets)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            cliAssembly, "run", contractPath, "--agent", "fake", "--scenario", "Stop", "--runs", runs,
            "--lifecycle-log", lifecycleLog, "--slack-notifications", "test", "--slack-config", slackConfiguration,
            "--slack-secrets", slackSecrets
        }) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Orchestrator CLIを開始できませんでした。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            throw new TimeoutException("Fake Stop通知テストが2分以内に終了しませんでした。");
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}

public sealed class OrchestratorSlackOptInFactAttribute : FactAttribute
{
    public OrchestratorSlackOptInFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ATELIERS_RUN_ORCHESTRATOR_SLACK_INTEGRATION_TESTS") != "1")
            Skip = "Slack Orchestrator実送信テストは既定でSkipされます。専用testChannelへの実送信を明示opt-inしてください。";
    }
}
