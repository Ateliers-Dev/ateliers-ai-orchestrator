using System.Text;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ateliers.Ai.Orchestrator.Core;
using Ateliers.Ai.Orchestrator.Cli;
using Ateliers.Slack;

if (args.Length >= 2 && args[0] == "run" && args[1] == "--stdin-json")
{
    Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
else if (args.Length == 1 && args[0] is "--capabilities" or "--contract-schema" or "--orchestration-guide")
{
    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}

return await OrchestratorCli.RunAsync(args);

/// <summary>タスク契約の検証と実行準備記録を扱うコマンドライン入口です。</summary>
internal static class OrchestratorCli
{
    /// <summary>引数を解析し、要求されたCLI操作を実行します。</summary>
    /// <param name="args">コマンド名とその引数です。</param>
    /// <returns>成功時は0、入力不備は2、実行記録の失敗は3です。</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        var structuredRun = args.Length >= 2 && args[0] == "run" && args[1] == "--stdin-json";
        if (args.Length == 1 && (args[0] is "--version" or "version"))
        {
            Console.WriteLine(GetVersion());
            return 0;
        }

        if (args.Length == 1 && args[0] == "--capabilities")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { cliVersion = GetVersion(), protocolVersion = 1, commands = new { runStdinJson = true, contractSchema = true, orchestrationGuide = true } }));
            return 0;
        }

        if (args.Length == 1 && args[0] == "--contract-schema")
        {
            Console.WriteLine(TaskContractSchema.GetJson());
            return 0;
        }

        if (args.Length == 1 && args[0] == "--orchestration-guide")
        {
            Console.Write(OrchestrationGuide.GetText());
            return 0;
        }

        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintUsage(Console.Out);
            return 0;
        }

        if (args[0] is "validate" or "start" or "run" or "verify-slack" && args.Skip(1).Any(argument => argument is "--help" or "-h"))
        {
            PrintUsage(Console.Out, args[0]);
            return 0;
        }

        if (args.Length < 2 || args[0] is not ("validate" or "start" or "run" or "verify-slack") ||
            (args[0] == "validate" && args.Length != 2) ||
            (args[0] == "verify-slack" && args.Length != 3) ||
            (args[0] == "start" && args.Length is not (2 or 4)) ||
            (args[0] == "run" && (args.Length < 2 || args.Length % 2 != 0)))
        {
            Console.Error.WriteLine("コマンドまたは引数が不足しているか、不正です。");
            if (structuredRun) WriteStructuredError("invalid_arguments", "コマンド引数が不正です。", 2);
            else PrintUsage(Console.Error);
            return 2;
        }

        if (args[0] == "verify-slack")
        {
            if (!TryResolveFullPath(args[1], "Slack設定", out var slackConfigurationPath, out var slackPathError) ||
                !TryResolveFullPath(args[2], "Slack secrets", out var slackSecretsPath, out slackPathError))
            {
                Console.Error.WriteLine(slackPathError);
                return 2;
            }

            using var dispatcher = SlackLifecycleNotificationDispatcher.TryCreate(slackConfigurationPath, slackSecretsPath, out var warning);
            if (dispatcher is null)
            {
                Console.Error.WriteLine(warning ?? "Slack設定を利用できません。");
                return 3;
            }
            if (!await dispatcher.VerifyBotIdentitiesAsync())
            {
                Console.Error.WriteLine("Slack認証を照合できません。Bot ID不一致またはAPIエラーです。通知は送信していません。");
                return 3;
            }
            Console.WriteLine("Slackの本番Bot認証と設定Bot IDを照合しました。通知は送信していません。");
            return 0;
        }

        ContractReadResult read;
        if (structuredRun)
        {
            read = TaskContractReader.ReadJson(await Console.In.ReadToEndAsync());
        }
        else if (!TryResolveFullPath(args[1], "契約ファイル", out var contractPath, out var pathError))
        {
            Console.Error.WriteLine(pathError);
            PrintUsage(Console.Error);
            return 2;
        }
        else read = TaskContractReader.Read(contractPath);
        if (!read.IsValid)
        {
            foreach (var error in read.Errors) Console.Error.WriteLine($"契約エラー: {error}");
            if (structuredRun) WriteStructuredError("invalid_contract", "契約JSONが不正です。", 2, read.Errors);
            return 2;
        }

        var contract = read.Contract!;
        var contractHash = read.ContentSha256!;

        if (args[0] == "validate")
        {
            Console.WriteLine($"契約は有効です: taskId={contract.TaskId} schemaVersion={contract.SchemaVersion} sha256={contractHash}");
            return 0;
        }

        if (args[0] == "run")
        {
            if (!TryParseRunOptions(args, structuredRun, contract.WorkingDirectory, out var scenario, out var runRoot, out var lifecycleLogPath, out var evidencePath, out var agentKind,
                out var codexOptions, out var notificationsDisabled, out var useSlackTestChannel, out var slackConfigurationPath,
                out var slackSecretsPath, out var optionError))
            {
                Console.Error.WriteLine(optionError);
                if (structuredRun) WriteStructuredError("invalid_arguments", optionError, 2);
                else PrintUsage(Console.Error, "run");
                return 2;
            }

            if (!TryReadReviewEvidence(evidencePath, out var evidence, out var evidenceError))
            {
                Console.Error.WriteLine(evidenceError);
                if (structuredRun) WriteStructuredError("invalid_review_evidence", evidenceError, 2);
                else PrintUsage(Console.Error, "run");
                return 2;
            }

            using var stopSource = new CancellationTokenSource();
            ConsoleCancelEventHandler? cancelHandler = null;
            if (agentKind == "codex")
            {
                cancelHandler = (_, eventArgs) => { eventArgs.Cancel = true; stopSource.Cancel(); };
                Console.CancelKeyPress += cancelHandler;
                codexOptions = codexOptions! with { CancellationToken = stopSource.Token };
            }

            var fakeRunId = Guid.NewGuid().ToString("N");
            var fakeRunDirectory = Path.Combine(runRoot, fakeRunId);
            try
            {
                Directory.CreateDirectory(fakeRunDirectory);
                using var eventStore = JsonlRunEventStore.CreateNew(Path.Combine(fakeRunDirectory, "events.jsonl"), fakeRunId);
                using var lifecycleWriter = slackConfigurationPath is null
                    ? new LifecycleNotificationEventWriter(lifecycleLogPath, notificationsDisabled, useSlackTestChannel)
                    : new LifecycleNotificationEventWriter(lifecycleLogPath, notificationsDisabled, slackConfigurationPath, slackSecretsPath!);
                IAgentRunner runner = agentKind == "codex" ? new CodexCliAgentRunner(codexOptions!) : new FakeAgentRunner(scenario);
                using var publisher = agentKind == "codex" && contract.Reporting.PullRequestExpected
                    ? new GitHubWorkflowPublisher() : null;
                var result = new FakeAgentWorkflow().Run(contract, contractHash, fakeRunId, fakeRunDirectory,
                    runner, eventStore, evidence, stopToken: stopSource.Token,
                    codexOneShot: agentKind == "codex", lifecycleEvents: lifecycleWriter, publisher: publisher);
                await lifecycleWriter.FlushSlackNotificationsAsync(stopSource.Token);
                if (lifecycleWriter.FailedWrites > 0)
                    Console.Error.WriteLine($"警告: ライフサイクルJSONLへの書き込みに{lifecycleWriter.FailedWrites}件失敗しました。実行結果は継続しています。");
                if (lifecycleWriter.FailedSlackSends > 0)
                {
                    try { Console.Error.WriteLine($"警告: Slack通知に{lifecycleWriter.FailedSlackSends}件失敗しました。実行結果は継続しています。"); }
                    catch (Exception) { }
                }
                if (!structuredRun && lifecycleWriter.SentSlackSends > 0)
                    Console.WriteLine($"Slack通知送信成功: {lifecycleWriter.SentSlackSends}件");
                if (!result.PersistenceSucceeded)
                {
                    Console.Error.WriteLine($"実行は人間判断待ちで停止しましたが、成果物または実行記録の保存に失敗しました: {fakeRunDirectory}");
                    if (result.Detail is not null) Console.Error.WriteLine(result.Detail);
                    if (structuredRun) WriteStructuredResult(false, 3, fakeRunId, result.State.ToString(), fakeRunDirectory,
                        "persistence_failed", "実行記録または成果物の保存に失敗しました。");
                    return 3;
                }
                var exitCode = result.State == RunState.Failed ? 3 : 0;
                if (structuredRun)
                    WriteStructuredResult(exitCode == 0, exitCode, fakeRunId, result.State.ToString(), fakeRunDirectory,
                        exitCode == 0 ? null : "workflow_failed", exitCode == 0 ? null : "オーケストレーションが失敗しました。");
                else
                {
                    Console.WriteLine($"{(agentKind == "codex" ? "Codex CLI実行" : "Fake実行")}を記録しました: {fakeRunId}");
                    Console.WriteLine($"状態: {result.State}");
                    Console.WriteLine($"記録先: {fakeRunDirectory}");
                }
                return exitCode;
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine($"Fake実行の成果物または記録を書き込めませんでした: {exception.Message}");
                Console.Error.WriteLine($"記録先: {fakeRunDirectory}（部分的な記録が残っている場合は内容を確認してください）");
                if (structuredRun) WriteStructuredResult(false, 3, fakeRunId, null, fakeRunDirectory, "persistence_failed", "実行記録を書き込めませんでした。");
                return 3;
            }
            catch (UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Fake実行の保存先への書き込み権限がありません: {fakeRunDirectory}");
                if (structuredRun) WriteStructuredResult(false, 3, fakeRunId, null, fakeRunDirectory, "persistence_failed", "実行記録先への書き込み権限がありません。");
                return 3;
            }
            catch (JsonException exception)
            {
                Console.Error.WriteLine($"Fake実行記録をJSONへ変換できませんでした: {exception.Message}");
                if (structuredRun) WriteStructuredResult(false, 3, fakeRunId, null, fakeRunDirectory, "persistence_failed", "実行記録をJSONへ変換できませんでした。");
                return 3;
            }
            finally
            {
                if (cancelHandler is not null) Console.CancelKeyPress -= cancelHandler;
            }
        }

        string runsDirectory;
        if (!TryGetRunsDirectory(args, out runsDirectory, out var runsPathError))
        {
            Console.Error.WriteLine(runsPathError);
            PrintUsage(Console.Error, "start");
            return 2;
        }

        var runId = Guid.NewGuid().ToString("N");
        var runDirectory = Path.Combine(runsDirectory, runId);
        var elapsed = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(runDirectory);
            using var eventStore = JsonlRunEventStore.CreateNew(Path.Combine(runDirectory, "events.jsonl"), runId);
            eventStore.Append("run_prepared", "accepted", to: RunState.Prepared,
                detail: "契約を検証し、実行記録を初期化しました。Agent実行はこのIssueの範囲外です。",
                inputReference: $"sha256:{contractHash}",
                unknownInformation: ["Agent実行・モデル情報は未取得", "成果物のcommit・差分は未取得", "PR提出は未実施", "人間のマージは未実施"]);

            var reportPath = Path.Combine(runDirectory, "report.md");
            var report = string.Join(Environment.NewLine,
            [
                "# 実行準備結果",
                "",
                $"- 実行ID: `{runId}`",
                $"- タスクID: `{contract.TaskId}`",
                "- 状態: `Prepared`",
                $"- 契約SHA-256: `{contractHash}`",
                "- Agent実行: 未実施（Issue #3 は契約・状態・記録の骨格まで）",
                "- PR提出: 未実施",
                "- 人間のマージ: 未実施",
                "",
                "契約JSON本文は、機密情報を無差別に複製しないため実行記録へコピーしていません。イベント記録は完全な監査台帳ではありません。",
                ""
            ]);
            await File.WriteAllTextAsync(reportPath, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            eventStore.Append("report_written", "accepted", to: RunState.Prepared,
                outputReference: Path.GetRelativePath(runDirectory, reportPath),
                elapsedMilliseconds: elapsed.ElapsedMilliseconds,
                unknownInformation: ["Agent実行・モデル情報は未取得", "成果物のcommit・差分は未取得"]);

            Console.WriteLine($"実行準備を記録しました: {runId}");
            Console.WriteLine("状態: Prepared。Agent実行、PR提出、人間のマージは行っていません。");
            Console.WriteLine($"記録先: {runDirectory}");
            return 0;
        }

        catch (IOException exception)
        {
            Console.Error.WriteLine($"実行記録またはレポートを書き込めませんでした: {exception.Message}");
            Console.Error.WriteLine($"記録先: {runDirectory}（部分的な記録が残っている場合は内容を確認してください）");
            return 3;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"実行記録先への書き込み権限がありません: {runDirectory}");
            return 3;
        }
    }

    /// <summary>実行アセンブリに埋め込まれたInformationalVersionを返します。</summary>
    internal static string GetVersion()
    {
        var assembly = typeof(OrchestratorCli).Assembly;
        return assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static void WriteStructuredError(string code, string message, int exitCode, IReadOnlyList<string>? details = null) =>
        Console.WriteLine(JsonSerializer.Serialize(new { protocolVersion = 1, ok = false, exitCode, error = new { code, message, details } }));

    private static void WriteStructuredResult(bool ok, int exitCode, string? runId, string? state, string? runDirectory, string? errorCode, string? message) =>
        Console.WriteLine(JsonSerializer.Serialize(new { protocolVersion = 1, ok, exitCode, runId, state, runDirectory,
            error = errorCode is null ? null : new { code = errorCode, message } }, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }));

    /// <summary>startコマンドの引数から実行記録の保存先を解決します。</summary>
    /// <param name="args">解析済みCLI引数です。</param>
    /// <param name="runsDirectory">実行記録の保存先となる絶対パスです。</param>
    /// <param name="error">引数またはパスが不正な場合の説明です。</param>
    /// <returns>引数とパスを使用できる場合はtrueです。</returns>
    private static bool TryGetRunsDirectory(string[] args, out string runsDirectory, out string error)
    {
        if (args.Length == 2)
            return TryResolveFullPath(Path.Combine(".ateliers-orchestrator", "runs"), "実行記録先", out runsDirectory, out error);
        if (args.Length == 4 && args[2] == "--runs")
            return TryResolveFullPath(args[3], "--runs", out runsDirectory, out error);
        runsDirectory = string.Empty;
        error = "start の任意引数は --runs <ディレクトリ> です。";
        return false;
    }

    /// <summary>Fake実行のシナリオと保存先、外部証跡入力を解析します。</summary>
    /// <param name="args">コマンド引数です。</param>
    /// <param name="scenario">選択されたFake Agentシナリオです。</param>
    /// <param name="runsDirectory">実行記録の親ディレクトリです。</param>
    /// <param name="evidencePath">指定された人間確認証跡ファイルです。</param>
    /// <param name="error">解析失敗時の説明です。</param>
    /// <returns>すべての引数が認識できた場合はtrueです。</returns>
    private static bool TryParseRunOptions(string[] args, bool structuredRun, string workingDirectory, out FakeScenario scenario, out string runsDirectory,
        out string lifecycleLogPath, out string? evidencePath, out string agentKind, out CodexCliRunnerOptions? codexOptions,
        out bool notificationsDisabled, out bool useSlackTestChannel, out string? slackConfigurationPath,
        out string? slackSecretsPath, out string error)
    {
        scenario = FakeScenario.Success;
        var runBaseDirectory = structuredRun ? workingDirectory : Environment.CurrentDirectory;
        runsDirectory = Path.GetFullPath(Path.Combine(runBaseDirectory, ".ateliers-orchestrator", "runs"));
        lifecycleLogPath = Path.GetFullPath(Path.Combine(runBaseDirectory, ".ateliers-orchestrator", "lifecycle.jsonl"));
        evidencePath = null;
        agentKind = "fake";
        codexOptions = null;
        notificationsDisabled = false;
        useSlackTestChannel = false;
        slackConfigurationPath = null;
        slackSecretsPath = null;
        error = string.Empty;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? executable = null, implementerHome = null, reviewerHome = null;
        for (var index = 2; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !seen.Add(args[index]))
            {
                error = "run のオプションは重複しない名前と値の組で指定してください。";
                return false;
            }

            switch (args[index])
            {
                case "--scenario":
                    if (!Enum.TryParse(args[index + 1], ignoreCase: true, out scenario) || !Enum.IsDefined(scenario))
                    {
                        error = $"未対応のFakeシナリオです: {args[index + 1]}";
                        return false;
                    }
                    break;
                case "--agent":
                    if (args[index + 1] is not ("fake" or "codex"))
                    {
                        error = "--agent は fake または codex です。";
                        return false;
                    }
                    agentKind = args[index + 1];
                    break;
                case "--codex-exe":
                    if (!TryResolveFullPath(args[index + 1], "--codex-exe", out executable, out error)) return false;
                    break;
                case "--implementer-codex-home":
                    if (!TryResolveFullPath(args[index + 1], "--implementer-codex-home", out implementerHome, out error)) return false;
                    break;
                case "--reviewer-codex-home":
                    if (!TryResolveFullPath(args[index + 1], "--reviewer-codex-home", out reviewerHome, out error)) return false;
                    break;
                case "--runs":
                    if (!TryResolveFullPath(args[index + 1], "--runs", out var runPath, out error)) return false;
                    runsDirectory = runPath;
                    break;
                case "--lifecycle-log":
                    if (!TryResolveFullPath(args[index + 1], "--lifecycle-log", out lifecycleLogPath, out error)) return false;
                    break;
                case "--review-evidence":
                    if (!TryResolveFullPath(args[index + 1], "--review-evidence", out var reviewPath, out error)) return false;
                    evidencePath = reviewPath;
                    break;
                case "--slack-notifications":
                    if (args[index + 1] is not ("auto" or "disabled" or "test"))
                    {
                        error = "--slack-notifications は auto、disabled、test のいずれかを指定してください。";
                        return false;
                    }
                    notificationsDisabled = args[index + 1] == "disabled";
                    useSlackTestChannel = args[index + 1] == "test";
                    break;
                case "--slack-config":
                    if (!TryResolveFullPath(args[index + 1], "--slack-config", out var configurationPath, out error)) return false;
                    slackConfigurationPath = configurationPath;
                    break;
                case "--slack-secrets":
                    if (!TryResolveFullPath(args[index + 1], "--slack-secrets", out var secretsPath, out error)) return false;
                    slackSecretsPath = secretsPath;
                    break;
                default:
                    error = $"未対応のrunオプションです: {args[index]}";
                    return false;
            }
        }
        if ((slackConfigurationPath is null) != (slackSecretsPath is null))
        {
            error = "--slack-config と --slack-secrets は両方指定してください。";
            return false;
        }
        if ((slackConfigurationPath is not null || slackSecretsPath is not null) && (notificationsDisabled || useSlackTestChannel))
        {
            error = "Slack設定パスを指定する場合、slack-notifications は auto にしてください。";
            return false;
        }
        if (agentKind == "codex")
        {
            if (seen.Contains("--scenario")) { error = "--scenario は --agent fake と組み合わせてください。"; return false; }
            if (executable is null)
            { error = "Codex実行には --codex-exe が必要です。"; return false; }
            try
            {
                codexOptions = new CodexCliRunnerOptions(executable,
                    CodexCliRunnerOptions.ResolveCodexHome(implementerHome),
                    CodexCliRunnerOptions.ResolveCodexHome(reviewerHome)).Validate();
            }
            catch (ArgumentException exception) { error = exception.Message; return false; }
        }
        return true;
    }

    /// <summary>外部証跡ファイルを実行開始前に読み込み、入力不備を報告します。</summary>
    /// <param name="evidencePath">証跡JSONの絶対パスです。未指定ならnullです。</param>
    /// <param name="evidence">読み込んだ証跡です。</param>
    /// <param name="error">読み込みやJSON形式に問題がある場合の説明です。</param>
    /// <returns>証跡が未指定、または正常に読み込めた場合はtrueです。</returns>
    private static bool TryReadReviewEvidence(string? evidencePath, out HumanReviewEvidence? evidence, out string error)
    {
        evidence = null;
        error = string.Empty;
        if (evidencePath is null) return true;

        try
        {
            evidence = JsonSerializer.Deserialize<HumanReviewEvidence>(File.ReadAllBytes(evidencePath), new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = false,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            });
            if (evidence is not null) return true;
            error = "人間確認証跡JSONが空です。";
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            error = exception is JsonException
                ? $"人間確認証跡JSONが不正です: {exception.Message}"
                : $"人間確認証跡ファイルを読み取れません: {exception.Message}";
            return false;
        }
    }

    /// <summary>必須パス値を検証して絶対パスへ変換します。</summary>
    /// <param name="value">入力されたパスです。</param>
    /// <param name="optionName">エラー表示に使う引数名です。</param>
    /// <param name="fullPath">変換後の絶対パスです。</param>
    /// <param name="error">空または不正なパスの場合の説明です。</param>
    /// <returns>パスを使用できる場合はtrueです。</returns>
    private static bool TryResolveFullPath(string value, string optionName, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"{optionName} に空のパスは指定できません。";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(value);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"{optionName} のパスが不正です: {exception.Message}";
            return false;
        }
    }

    /// <summary>利用できるコマンド形式を指定された出力先へ表示します。</summary>
    private static void PrintUsage(TextWriter writer, string? command = null)
    {
        writer.WriteLine(command switch
        {
            "validate" => "使用方法: ateliers-orchestrator validate <contract.json>",
            "verify-slack" => "使用方法: ateliers-orchestrator verify-slack <slack.json> <slack.secrets.json>",
            "start" => "使用方法: ateliers-orchestrator start <contract.json> [--runs <ディレクトリ>]",
            "run" => "使用方法: ateliers-orchestrator run <contract.json> [--agent fake|codex] [--scenario <FakeScenario>] [--codex-exe <絶対パス>] [--implementer-codex-home <絶対パス>] [--reviewer-codex-home <絶対パス>] [--runs <ディレクトリ>] [--lifecycle-log <JSONL>] [--review-evidence <証跡.json>] [--slack-notifications auto|disabled|test] [--slack-config <JSON>] [--slack-secrets <JSON>]",
            _ => "使用方法:"
        });
        if (command is not null) return;
        writer.WriteLine("  Ateliers.Ai.Orchestrator.Cli --version");
        writer.WriteLine("  Ateliers.Ai.Orchestrator.Cli --capabilities");
        writer.WriteLine("  Ateliers.Ai.Orchestrator.Cli --contract-schema");
        writer.WriteLine("  Ateliers.Ai.Orchestrator.Cli --orchestration-guide");
        writer.WriteLine("  ateliers-orchestrator validate <contract.json>");
        writer.WriteLine("  ateliers-orchestrator verify-slack <slack.json> <slack.secrets.json>");
        writer.WriteLine("  ateliers-orchestrator start <contract.json> [--runs <ディレクトリ>]");
        writer.WriteLine("  ateliers-orchestrator run <contract.json> [--agent fake|codex] [--scenario <FakeScenario>] [--codex-exe <絶対パス>] [--implementer-codex-home <絶対パス>] [--reviewer-codex-home <絶対パス>] [--runs <ディレクトリ>] [--lifecycle-log <JSONL>] [--review-evidence <証跡.json>] [--slack-notifications auto|disabled|test] [--slack-config <JSON>] [--slack-secrets <JSON>]");
    }
}

