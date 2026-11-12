using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Sdk;

namespace Ateliers.Ai.Orchestrator.IntegrationTests;

public sealed class CodexIntegrationTests
{
    private const string OptInVariable = "ATELIERS_RUN_CODEX_INTEGRATION_TESTS";
    private const string CodexPathVariable = "ATELIERS_CODEX_CLI_PATH";

    [CodexOptInFact]
    [Trait("Category", "ExternalIntegration")]
    public async Task RunAsync_ShouldCompleteImplementerReviewerAndPersistNeedsHumanInput()
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
            run = await InvokeOrchestratorAsync(cliAssembly, Path.GetFullPath(codexExecutable!), fixture);
            Assert.True(run.ExitCode == 0,
                $"Orchestrator終了コードが0ではありません: {run.ExitCode}");

            var runDirectories = Directory.GetDirectories(fixture.RunsDirectory);
            Assert.True(runDirectories.Length == 1, $"実行記録ディレクトリ数が1ではありません: {runDirectories.Length}");
            runDirectory = runDirectories[0];

            var reportPath = Path.Combine(runDirectory, "report.md");
            Assert.True(File.Exists(reportPath), "report.mdがありません。");
            var report = await File.ReadAllTextAsync(reportPath);
            Assert.Contains("- 状態: `NeedsHumanInput`", report, StringComparison.Ordinal);
            Assert.Contains("- 実装呼び出し: 1", report, StringComparison.Ordinal);
            Assert.Contains("- レビュー呼び出し: 1", report, StringComparison.Ordinal);

            var eventsPath = Path.Combine(runDirectory, "events.jsonl");
            Assert.True(File.Exists(eventsPath), "events.jsonlがありません。");
            var events = (await File.ReadAllLinesAsync(eventsPath)).Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.NotEmpty(events);
                var runId = events[0].RootElement.GetProperty("runId").GetString();
                for (var index = 0; index < events.Length; index++)
                {
                    var item = events[index].RootElement;
                    Assert.Equal(runId, item.GetProperty("runId").GetString());
                    Assert.Equal(index + 1, item.GetProperty("sequence").GetInt64());
                }

                var all = events.Select(item => item.RootElement).ToArray();
                var calls = all.Where(item => item.GetProperty("eventType").GetString() == "agent_call").ToArray();
                var implementerCalls = calls.Where(item => item.GetProperty("role").GetString() == "Implementer").ToArray();
                var reviewerCalls = calls.Where(item => item.GetProperty("role").GetString() == "Reviewer").ToArray();
                Assert.Single(implementerCalls);
                Assert.Single(reviewerCalls);
                Assert.Equal("completed", implementerCalls[0].GetProperty("result").GetString());
                var reviewerResult = reviewerCalls[0].GetProperty("result").GetString();
                Assert.True(reviewerResult is "completed" or "empty", "Reviewerのイベント記録が成功ではありません。");
                var reviewerOutput = reviewerCalls[0].TryGetProperty("outputReference", out var reviewerOutputElement) && reviewerOutputElement.ValueKind == JsonValueKind.String;
                var reviewerArtifacts = reviewerCalls[0].GetProperty("artifactReferences").GetArrayLength() > 0;
                Assert.True(reviewerOutput || reviewerArtifacts, "Reviewer応答・成果物のどちらも記録されていません。");

                var stopped = Assert.Single(all, item => item.GetProperty("eventType").GetString() == "codex_one_shot_stopped");
                Assert.Equal("needs_human_input", stopped.GetProperty("result").GetString());
                Assert.Contains(all, item => item.GetProperty("eventType").GetString() == "state_transition" &&
                    item.GetProperty("toState").GetString() == "needsHumanInput");
                Assert.Contains(all, item => item.GetProperty("eventType").GetString() == "report_written" &&
                    item.GetProperty("result").GetString() == "accepted" && item.GetProperty("outputReference").GetString() == "report.md");

                ValidateAgentEvidence(runDirectory, implementerCalls[0], "implementer", requireResponse: true);
                ValidateAgentEvidence(runDirectory, reviewerCalls[0], "reviewer", requireResponse: false);
                var reviewerEvidencePath = Path.Combine(runDirectory, "exchanges", "reviewer-1", "execution-evidence.json");
                string? reviewerArtifactSha256;
                using (var reviewerEvidence = JsonDocument.Parse(await File.ReadAllBytesAsync(reviewerEvidencePath)))
                {
                    var reviewerWorkingDirectory = reviewerEvidence.RootElement.GetProperty("WorkingDirectory").GetString();
                    var expectedReviewerWorkspace = Path.Combine(runDirectory, "exchanges", "reviewer-1", "workspace");
                    Assert.True(StringComparer.OrdinalIgnoreCase.Equals(
                        Path.GetFullPath(expectedReviewerWorkspace), Path.GetFullPath(reviewerWorkingDirectory!)),
                        "Reviewerの作業ディレクトリが実行記録内の専用workspaceではありません。");
                    Assert.False(Path.GetFullPath(reviewerWorkingDirectory!).StartsWith(
                        Path.GetFullPath(fixture.RepositoryDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                        "Reviewerの作業ディレクトリがImplementerのcheckout内です。");
                    Assert.True(StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(reviewerWorkingDirectory!),
                        Path.GetFullPath((await RunGitAsync(reviewerWorkingDirectory!, "rev-parse", "--show-toplevel")).Trim())),
                        "Reviewerの作業ディレクトリが独立したGitルートではありません。");
                    reviewerArtifactSha256 = reviewerEvidence.RootElement.GetProperty("ArtifactSha256").GetString();
                }
                ValidateReferences(runDirectory, implementerCalls[0]);
                ValidateReferences(runDirectory, reviewerCalls[0]);

                var fixedArtifact = Assert.Single(all, item => item.GetProperty("eventType").GetString() == "artifact_fixed");
                var artifactReference = fixedArtifact.GetProperty("outputReference").GetString();
                Assert.False(string.IsNullOrWhiteSpace(artifactReference), "固定成果物の参照がありません。");
                var artifactPath = ResolveRunReference(runDirectory, artifactReference!);
                Assert.True(File.Exists(artifactPath), $"固定成果物がありません: {artifactReference}");
                var artifact = await File.ReadAllTextAsync(artifactPath);
                var frozenDiff = ExtractUnifiedDiff(artifact);
                Assert.Equal(fixedArtifact.GetProperty("diffSha256").GetString(), reviewerArtifactSha256);
                var finalDiff = await RunGitAsync(fixture.RepositoryDirectory, "diff", "--binary", "--no-ext-diff", "HEAD", "--");
                Assert.Equal(NormalizeNewlines(frozenDiff), NormalizeNewlines(finalDiff));
                Assert.Equal(fixedArtifact.GetProperty("diffSha256").GetString(), HashSha256(await File.ReadAllBytesAsync(artifactPath)));

                var changedPaths = ParsePorcelainPaths(await RunGitAsync(fixture.RepositoryDirectory, "status", "--porcelain", "-z", "--untracked-files=all"));
                Assert.Equal([IsolatedFixture.TargetFile], changedPaths);
                var targetContents = await File.ReadAllTextAsync(Path.Combine(fixture.RepositoryDirectory, IsolatedFixture.TargetFile));
                Assert.Contains(IsolatedFixture.ExpectedMarker, targetContents, StringComparison.Ordinal);
                Assert.Equal(IsolatedFixture.SeedSentinel,
                    await File.ReadAllTextAsync(Path.Combine(fixture.RepositoryDirectory, IsolatedFixture.SentinelFile)));
            }
            finally
            {
                foreach (var item in events) item.Dispose();
            }

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
                fixture.Dispose();
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
            "--runs", fixture.RunsDirectory
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

    private static void ValidateAgentEvidence(string runDirectory, JsonElement call, string role, bool requireResponse)
    {
        var evidencePath = Path.Combine(runDirectory, "exchanges", $"{role}-1", "execution-evidence.json");
        Assert.True(File.Exists(evidencePath), $"{role}の終了証跡ファイルがありません。");
        using var evidenceDocument = JsonDocument.Parse(File.ReadAllBytes(evidencePath));
        var evidence = evidenceDocument.RootElement;
        Assert.True(evidence.GetProperty("ProcessExitObserved").GetBoolean(), $"{role}のプロセス終了を確認できませんでした。");
        Assert.True(evidence.GetProperty("StreamsClosed").GetBoolean(), $"{role}の出力stream終了を確認できませんでした。");
        Assert.Equal("turn.completed", evidence.GetProperty("TerminalEvent").GetString());
        Assert.Equal(0, evidence.GetProperty("ExitCode").GetInt32());
        Assert.True(evidence.GetProperty("ProcessId").GetInt32() > 0, $"{role}のプロセスIDがありません。");
        Assert.False(evidence.GetProperty("StopRequested").GetBoolean() || evidence.GetProperty("TimedOut").GetBoolean(),
            $"{role}が停止要求またはtimeoutで終了しました。");

        var stdoutReference = evidence.GetProperty("StandardOutputReference").GetString();
        var stderrReference = evidence.GetProperty("StandardErrorReference").GetString();
        Assert.True(stdoutReference is not null && File.Exists(ResolveRunReference(runDirectory, stdoutReference)), $"{role}のJSONL応答ログ参照が不正です。");
        Assert.True(stderrReference is not null && File.Exists(ResolveRunReference(runDirectory, stderrReference)), $"{role}のstderrログ参照が不正です。");
        if (requireResponse)
        {
            var outputReference = call.GetProperty("outputReference").GetString();
            Assert.True(outputReference is not null && File.Exists(ResolveRunReference(runDirectory, outputReference)), $"{role}応答参照が不正です。");
            Assert.True(new FileInfo(ResolveRunReference(runDirectory, outputReference!)).Length > 0, $"{role}応答が空です。");
        }
    }

    private static void ValidateReferences(string runDirectory, JsonElement call)
    {
        if (call.TryGetProperty("outputReference", out var output) && output.ValueKind == JsonValueKind.String)
            Assert.True(File.Exists(ResolveRunReference(runDirectory, output.GetString()!)), "Agent応答のoutputReferenceが実在しません。");
        if (!call.TryGetProperty("artifactReferences", out var references)) return;
        foreach (var reference in references.EnumerateArray())
            Assert.True(File.Exists(ResolveRunReference(runDirectory, reference.GetString()!)), $"任意成果物の参照先が不正です: {reference.GetString()}");
    }

    private static string ResolveRunReference(string runDirectory, string reference)
    {
        var path = Path.GetFullPath(Path.Combine(runDirectory, reference.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(runDirectory) + Path.DirectorySeparatorChar;
        Assert.True(path.StartsWith(root, StringComparison.OrdinalIgnoreCase), $"実行記録の参照がrunディレクトリ外を指しています: {reference}");
        return path;
    }

    private static string ExtractUnifiedDiff(string artifact)
    {
        const string start = "```diff\n";
        var index = artifact.IndexOf(start, StringComparison.Ordinal);
        Assert.True(index >= 0, "固定成果物にunified diffがありません。");
        index += start.Length;
        var end = artifact.IndexOf("\n```", index, StringComparison.Ordinal);
        Assert.True(end >= 0, "固定成果物のdiff囲みが閉じていません。");
        return artifact[index..end];
    }

    private static string[] ParsePorcelainPaths(string status) => status.Split('\0', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Length >= 4 ? line[3..] : line).Order(StringComparer.Ordinal).ToArray();

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

    private static string HashSha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class IsolatedFixture : IDisposable
    {
        public const string TargetFile = "target.txt";
        public const string SentinelFile = "sentinel.txt";
        public const string SeedSentinel = "do not change\n";
        public const string ExpectedMarker = "integration-test-marker-2026";

        public string RootDirectory { get; } = Path.Combine(Path.GetTempPath(), "ateliers-codex-integration", Guid.NewGuid().ToString("N"));
        public string RepositoryDirectory => Path.Combine(RootDirectory, "repository");
        public string RunsDirectory => Path.Combine(RootDirectory, "runs");
        public string ContractPath => Path.Combine(RootDirectory, "task-contract.json");

        public void CreateSeedRepository()
        {
            Directory.CreateDirectory(RepositoryDirectory);
            Directory.CreateDirectory(RunsDirectory);
            File.WriteAllText(Path.Combine(RepositoryDirectory, TargetFile), "seed\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(RepositoryDirectory, SentinelFile), SeedSentinel, new UTF8Encoding(false));
            RunGit("init", "--initial-branch=main");
            RunGit("config", "user.name", "Codex Integration Test");
            RunGit("config", "user.email", "integration@example.invalid");
            RunGit("config", "core.autocrlf", "false");
            RunGit("add", TargetFile, SentinelFile);
            RunGit("commit", "-m", "seed integration fixture");
        }

        public void WriteTaskContract()
        {
            var contract = new
            {
                schemaVersion = 1,
                taskId = "codex-integration-test",
                purpose = "隔離された一時Gitリポジトリで指定ファイルだけを変更し、レビュー結果を記録する",
                workingDirectory = RepositoryDirectory,
                scope = new { included = new[] { TargetFile }, excluded = new[] { SentinelFile, "その他のファイル", "外部書込・commit・push・PR" } },
                inputs = new[] { new { description = "初期seedファイル", reference = TargetFile, revision = "一時リポジトリのHEAD" } },
                requirements = new[] { $"{TargetFile}に一度だけ {ExpectedMarker} を含む1行を追加する", "指定ファイル以外は変更しない", "レビュー後も対象差分を変更しない" },
                acceptanceCriteria = new[] { "指定ファイルにmarker行がある", "対象外ファイルが変更されていない" },
                delegatedOperations = new[] { $"{TargetFile}だけを編集する", "成果物のレビューを行う" },
                limits = new { maxDurationMinutes = 10, maxRevisionAttempts = 0, maxRetries = 0 },
                stopConditions = new[] { "指定ファイル以外への変更が必要な場合は停止する" },
                reporting = new { pullRequestExpected = false, finalReportFormat = "markdown" }
            };
            File.WriteAllText(ContractPath, JsonSerializer.Serialize(contract, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }

        private void RunGit(params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = RepositoryDirectory, UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Git seed processを開始できませんでした。");
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"git {arguments[0]} に失敗しました: {error}");
        }

        public void Dispose()
        {
            if (Directory.Exists(RootDirectory)) Directory.Delete(RootDirectory, recursive: true);
        }
    }
}

public sealed class CodexOptInFactAttribute : FactAttribute
{
    public CodexOptInFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ATELIERS_RUN_CODEX_INTEGRATION_TESTS") != "1")
            Skip = "実Codex連携テストは既定でSkipされます。明示opt-in手順はREADMEを参照してください。";
    }
}
