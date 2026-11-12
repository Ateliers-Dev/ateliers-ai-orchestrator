using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const string OptIn = "--run-codex";
if (args.Length != 2 || args[0] != OptIn || !Path.IsPathFullyQualified(args[1]) || !File.Exists(args[1]))
{
    Console.Error.WriteLine("実Codex IntegrationTestsは明示的な opt-in が必要です。使用法: dotnet run --project tests/Ateliers.Ai.Orchestrator.IntegrationTests -- --run-codex <Codex CLIの絶対パス>");
    return 2;
}

try
{
    await RunEndToEndAsync(Path.GetFullPath(args[1]));
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"IntegrationTests失敗: {exception.Message}");
    return 1;
}

static async Task RunEndToEndAsync(string codexExecutable)
{
    var fixture = new IsolatedFixture();
    var succeeded = false;
    try
    {
        fixture.CreateSeedRepository();
        fixture.WriteTaskContract();
        var cliAssembly = FindCliAssembly();
        var run = await InvokeOrchestratorAsync(cliAssembly, codexExecutable, fixture);
        Assert(run.ExitCode == 0, $"Orchestrator終了コードが0ではありません。stdout: {run.StandardOutput}\nstderr: {run.StandardError}");

        var runDirectories = Directory.GetDirectories(fixture.RunsDirectory);
        Assert(runDirectories.Length == 1, $"実行記録ディレクトリ数が1ではありません: {runDirectories.Length}");
        var runDirectory = runDirectories[0];
        Assert(run.StandardOutput.Contains("状態: NeedsHumanInput", StringComparison.Ordinal), "CLIがNeedsHumanInputを報告しませんでした。");

        var reportPath = Path.Combine(runDirectory, "report.md");
        Assert(File.Exists(reportPath), "report.mdがありません。");
        var report = await File.ReadAllTextAsync(reportPath);
        Assert(report.Contains("- 状態: `NeedsHumanInput`", StringComparison.Ordinal), "report.mdの状態がNeedsHumanInputではありません。");
        Assert(report.Contains("- 実装呼び出し: 1", StringComparison.Ordinal), "report.mdのImplementer呼び出し数が1ではありません。");
        Assert(report.Contains("- レビュー呼び出し: 1", StringComparison.Ordinal), "report.mdのReviewer呼び出し数が1ではありません。");

        var eventsPath = Path.Combine(runDirectory, "events.jsonl");
        var events = (await File.ReadAllLinesAsync(eventsPath)).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert(events.Length > 0, "events.jsonlが空です。");
            var runId = events[0].RootElement.GetProperty("runId").GetString();
            for (var index = 0; index < events.Length; index++)
            {
                var item = events[index].RootElement;
                Assert(item.GetProperty("runId").GetString() == runId, "events.jsonlに異なるrunIdがあります。");
                Assert(item.GetProperty("sequence").GetInt64() == index + 1, "events.jsonlのsequenceが連続していません。");
            }

            var all = events.Select(item => item.RootElement).ToArray();
            var calls = all.Where(item => item.GetProperty("eventType").GetString() == "agent_call").ToArray();
            var implementerCalls = calls.Where(item => item.GetProperty("role").GetString() == "Implementer").ToArray();
            var reviewerCalls = calls.Where(item => item.GetProperty("role").GetString() == "Reviewer").ToArray();
            Assert(implementerCalls.Length == 1, $"Implementer呼び出しが1回ではありません: {implementerCalls.Length}");
            Assert(reviewerCalls.Length == 1, $"Reviewer呼び出しが1回ではありません: {reviewerCalls.Length}");
            Assert(implementerCalls[0].GetProperty("result").GetString() == "completed", "Implementerのイベント記録が成功ではありません。");
            var reviewerResult = reviewerCalls[0].GetProperty("result").GetString();
            Assert(reviewerResult is "completed" or "empty", "Reviewerのイベント記録が成功ではありません。");
            var reviewerOutput = reviewerCalls[0].TryGetProperty("outputReference", out var reviewerOutputElement) && reviewerOutputElement.ValueKind == JsonValueKind.String;
            var reviewerArtifacts = reviewerCalls[0].GetProperty("artifactReferences").GetArrayLength() > 0;
            Assert(reviewerOutput || reviewerArtifacts, "Reviewer応答・成果物のどちらも記録されていません。");

            var stopped = all.Single(item => item.GetProperty("eventType").GetString() == "codex_one_shot_stopped");
            Assert(stopped.GetProperty("result").GetString() == "needs_human_input", "停止イベントの結果がNeedsHumanInputではありません。");
            Assert(all.Any(item => item.GetProperty("eventType").GetString() == "state_transition" &&
                item.GetProperty("toState").GetString() == "needsHumanInput"), "NeedsHumanInputへの状態遷移が記録されていません。");
            Assert(all.Any(item => item.GetProperty("eventType").GetString() == "report_written" &&
                item.GetProperty("result").GetString() == "accepted" && item.GetProperty("outputReference").GetString() == "report.md"),
                "report_writtenイベントとreport.md参照が整合しません。");

            ValidateAgentEvidence(runDirectory, implementerCalls[0], "implementer", requireResponse: true);
            ValidateAgentEvidence(runDirectory, reviewerCalls[0], "reviewer", requireResponse: false);
            ValidateReferences(runDirectory, implementerCalls[0]);
            ValidateReferences(runDirectory, reviewerCalls[0]);

            var fixedArtifact = all.Single(item => item.GetProperty("eventType").GetString() == "artifact_fixed");
            var artifactReference = fixedArtifact.GetProperty("outputReference").GetString();
            Assert(artifactReference is not null, "固定成果物の参照がありません。");
            var artifactPath = ResolveRunReference(runDirectory, artifactReference!);
            Assert(File.Exists(artifactPath), $"固定成果物がありません: {artifactReference}");
            var artifact = await File.ReadAllTextAsync(artifactPath);
            var frozenDiff = ExtractUnifiedDiff(artifact);
            var finalDiff = await RunGitAsync(fixture.RepositoryDirectory, "diff", "--binary", "--no-ext-diff", "HEAD", "--");
            Assert(NormalizeNewlines(frozenDiff) == NormalizeNewlines(finalDiff),
                "Reviewerに渡した固定差分と最終Git差分が異なります。レビュー中に変更があった可能性があります。");
            Assert(fixedArtifact.GetProperty("diffSha256").GetString() == HashSha256(await File.ReadAllBytesAsync(artifactPath)),
                "固定成果物イベントのSHA-256がファイルと一致しません。");

            var changedPaths = ParsePorcelainPaths(await RunGitAsync(fixture.RepositoryDirectory, "status", "--porcelain", "--untracked-files=all"));
            Assert(changedPaths.SequenceEqual([IsolatedFixture.TargetFile], StringComparer.Ordinal),
                $"契約で指定したファイル以外も変更されました: {string.Join(", ", changedPaths)}");
            var targetContents = await File.ReadAllTextAsync(Path.Combine(fixture.RepositoryDirectory, IsolatedFixture.TargetFile));
            Assert(targetContents.Contains(IsolatedFixture.ExpectedMarker, StringComparison.Ordinal), "指定ファイルに要求した変更がありません。");
            Assert(await File.ReadAllTextAsync(Path.Combine(fixture.RepositoryDirectory, IsolatedFixture.SentinelFile)) == IsolatedFixture.SeedSentinel,
                "ReviewerまたはImplementerが対象外seedファイルを変更しました。");
        }
        finally
        {
            foreach (var item in events) item.Dispose();
        }

        succeeded = true;
        Console.WriteLine("実Codex IntegrationTests成功: Implementer 1回 → Reviewer 1回 → NeedsHumanInput");
    }
    finally
    {
        if (succeeded)
        {
            fixture.Dispose();
        }
        else
        {
            Console.Error.WriteLine($"失敗時の証跡を一時ディレクトリに保全しました: {fixture.RootDirectory}");
        }
    }
}

static string FindCliAssembly()
{
    for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
    {
        var project = Path.Combine(current.FullName, "src", "Ateliers.Ai.Orchestrator.Cli", "bin", "Release", "net10.0", "Ateliers.Ai.Orchestrator.Cli.dll");
        if (File.Exists(project)) return project;
        var debugProject = Path.Combine(current.FullName, "src", "Ateliers.Ai.Orchestrator.Cli", "bin", "Debug", "net10.0", "Ateliers.Ai.Orchestrator.Cli.dll");
        if (File.Exists(debugProject)) return debugProject;
    }
    throw new FileNotFoundException("CLI assemblyが見つかりません。先にソリューションをビルドしてください。");
}

static async Task<ProcessResult> InvokeOrchestratorAsync(string cliAssembly, string codexExecutable, IsolatedFixture fixture)
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
        throw new TimeoutException("Orchestratorが12分以内に終了しませんでした。");
    }
    return new ProcessResult(process.ExitCode, await stdout, await stderr);
}

static void ValidateAgentEvidence(string runDirectory, JsonElement call, string role, bool requireResponse)
{
    var evidencePath = Path.Combine(runDirectory, "exchanges", $"{role}-1", "execution-evidence.json");
    Assert(File.Exists(evidencePath), $"{role}の終了証跡ファイルがありません。");
    using var evidenceDocument = JsonDocument.Parse(File.ReadAllBytes(evidencePath));
    var evidence = evidenceDocument.RootElement;
    Assert(evidence.GetProperty("processExitObserved").GetBoolean(), $"{role}のプロセス終了を確認できませんでした。");
    Assert(evidence.GetProperty("streamsClosed").GetBoolean(), $"{role}の出力stream終了を確認できませんでした。");
    Assert(evidence.GetProperty("terminalEvent").GetString() == "turn.completed", $"{role}のCodex terminal eventがturn.completedではありません。");
    Assert(evidence.GetProperty("exitCode").GetInt32() == 0, $"{role}のCodex終了コードが0ではありません。");
    Assert(evidence.GetProperty("processId").GetInt32() > 0, $"{role}のプロセスIDがありません。");
    Assert(!evidence.GetProperty("stopRequested").GetBoolean() && !evidence.GetProperty("timedOut").GetBoolean(), $"{role}が停止要求またはtimeoutで終了しました。");

    var stdoutReference = evidence.GetProperty("standardOutputReference").GetString();
    var stderrReference = evidence.GetProperty("standardErrorReference").GetString();
    Assert(stdoutReference is not null && File.Exists(ResolveRunReference(runDirectory, stdoutReference)), $"{role}のJSONL応答ログ参照が不正です。");
    Assert(stderrReference is not null && File.Exists(ResolveRunReference(runDirectory, stderrReference)), $"{role}のstderrログ参照が不正です。");
    if (requireResponse)
    {
        var outputReference = call.GetProperty("outputReference").GetString();
        Assert(outputReference is not null && File.Exists(ResolveRunReference(runDirectory, outputReference)), $"{role}応答参照が不正です。");
        Assert(new FileInfo(ResolveRunReference(runDirectory, outputReference!)).Length > 0, $"{role}応答が空です。");
    }
}

static void ValidateReferences(string runDirectory, JsonElement call)
{
    if (call.TryGetProperty("outputReference", out var output) && output.ValueKind == JsonValueKind.String)
        Assert(File.Exists(ResolveRunReference(runDirectory, output.GetString()!)), "Agent応答のoutputReferenceが実在しません。");
    if (!call.TryGetProperty("artifactReferences", out var references)) return;
    foreach (var reference in references.EnumerateArray())
        Assert(File.Exists(ResolveRunReference(runDirectory, reference.GetString()!)), $"任意成果物の参照先が不正です: {reference.GetString()}");
}

static string ResolveRunReference(string runDirectory, string reference)
{
    var path = Path.GetFullPath(Path.Combine(runDirectory, reference.Replace('/', Path.DirectorySeparatorChar)));
    var root = Path.GetFullPath(runDirectory) + Path.DirectorySeparatorChar;
    Assert(path.StartsWith(root, StringComparison.OrdinalIgnoreCase), $"実行記録の参照がrunディレクトリ外を指しています: {reference}");
    return path;
}

static string ExtractUnifiedDiff(string artifact)
{
    const string start = "```diff\n";
    var index = artifact.IndexOf(start, StringComparison.Ordinal);
    Assert(index >= 0, "固定成果物にunified diffがありません。");
    index += start.Length;
    var end = artifact.IndexOf("\n```", index, StringComparison.Ordinal);
    Assert(end >= 0, "固定成果物のdiff囲みが閉じていません。");
    return artifact[index..end] + "\n";
}

static string[] ParsePorcelainPaths(string status)
{
    return status.Split('\0', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Length >= 4 ? line[3..] : line)
        .Order(StringComparer.Ordinal).ToArray();
}

static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
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

static string HashSha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

sealed class IsolatedFixture : IDisposable
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
