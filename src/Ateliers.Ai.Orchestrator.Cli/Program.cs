using System.Text;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ateliers.Ai.Orchestrator.Core;

return await OrchestratorCli.RunAsync(args);

/// <summary>タスク契約の検証と実行準備記録を扱うコマンドライン入口です。</summary>
internal static class OrchestratorCli
{
    /// <summary>引数を解析し、要求されたCLI操作を実行します。</summary>
    /// <param name="args">コマンド名とその引数です。</param>
    /// <returns>成功時は0、入力不備は2、実行記録の失敗は3です。</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2 || args[0] is not ("validate" or "start" or "run") ||
            (args[0] == "validate" && args.Length != 2) ||
            (args[0] == "start" && args.Length is not (2 or 4)) ||
            (args[0] == "run" && (args.Length < 2 || args.Length % 2 != 0)))
        {
            PrintUsage();
            return 2;
        }

        if (!TryResolveFullPath(args[1], "契約ファイル", out var contractPath, out var pathError))
        {
            Console.Error.WriteLine(pathError);
            PrintUsage();
            return 2;
        }
        var read = TaskContractReader.Read(contractPath);
        if (!read.IsValid)
        {
            foreach (var error in read.Errors) Console.Error.WriteLine($"契約エラー: {error}");
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
            if (!TryParseRunOptions(args, out var scenario, out var runRoot, out var evidencePath, out var agentKind,
                out var codexOptions, out var optionError))
            {
                Console.Error.WriteLine(optionError);
                PrintUsage();
                return 2;
            }

            if (!TryReadReviewEvidence(evidencePath, out var evidence, out var evidenceError))
            {
                Console.Error.WriteLine(evidenceError);
                PrintUsage();
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
                IAgentRunner runner = agentKind == "codex" ? new CodexCliAgentRunner(codexOptions!) : new FakeAgentRunner(scenario);
                var result = new FakeAgentWorkflow().Run(contract, contractHash, fakeRunId, fakeRunDirectory,
                    runner, eventStore, evidence, codexOneShot: agentKind == "codex");
                Console.WriteLine($"{(agentKind == "codex" ? "Codex CLI実行" : "Fake実行")}を記録しました: {fakeRunId}");
                Console.WriteLine($"状態: {result.State}");
                Console.WriteLine($"記録先: {fakeRunDirectory}");
                return result.State == RunState.Failed ? 3 : 0;
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine($"Fake実行の成果物または記録を書き込めませんでした: {exception.Message}");
                Console.Error.WriteLine($"記録先: {fakeRunDirectory}（部分的な記録が残っている場合は内容を確認してください）");
                return 3;
            }
            catch (UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Fake実行の保存先への書き込み権限がありません: {fakeRunDirectory}");
                return 3;
            }
            catch (JsonException exception)
            {
                Console.Error.WriteLine($"Fake実行記録をJSONへ変換できませんでした: {exception.Message}");
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
            PrintUsage();
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
    private static bool TryParseRunOptions(string[] args, out FakeScenario scenario, out string runsDirectory,
        out string? evidencePath, out string agentKind, out CodexCliRunnerOptions? codexOptions, out string error)
    {
        scenario = FakeScenario.Success;
        runsDirectory = Path.GetFullPath(Path.Combine(".ateliers-orchestrator", "runs"));
        evidencePath = null;
        agentKind = "fake";
        codexOptions = null;
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
                case "--review-evidence":
                    if (!TryResolveFullPath(args[index + 1], "--review-evidence", out var reviewPath, out error)) return false;
                    evidencePath = reviewPath;
                    break;
                default:
                    error = $"未対応のrunオプションです: {args[index]}";
                    return false;
            }
        }
        if (agentKind == "codex")
        {
            if (seen.Contains("--scenario")) { error = "--scenario は --agent fake と組み合わせてください。"; return false; }
            if (executable is null || implementerHome is null || reviewerHome is null)
            { error = "Codex実行には --codex-exe、--implementer-codex-home、--reviewer-codex-home が必要です。"; return false; }
            try { codexOptions = new CodexCliRunnerOptions(executable, implementerHome, reviewerHome).Validate(); }
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

    /// <summary>利用できるコマンド形式を標準エラーへ表示します。</summary>
    private static void PrintUsage()
    {
        Console.Error.WriteLine("使用方法:");
        Console.Error.WriteLine("  ateliers-orchestrator validate <contract.json>");
        Console.Error.WriteLine("  ateliers-orchestrator start <contract.json> [--runs <ディレクトリ>]");
        Console.Error.WriteLine("  ateliers-orchestrator run <contract.json> [--agent fake|codex] [--scenario <FakeScenario>] [--codex-exe <絶対パス> --implementer-codex-home <絶対パス> --reviewer-codex-home <絶対パス>] [--runs <ディレクトリ>] [--review-evidence <証跡.json>]");
    }
}
