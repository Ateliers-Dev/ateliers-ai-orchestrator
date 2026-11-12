using System.Text.Json;
using System.Security.Cryptography;
using System.Diagnostics;
using Ateliers.Ai.Orchestrator.Core;

if (args.Length >= 1 && args[0] == "--fake-codex-fixture") return RunFakeCodexFixture(args);
if (args.Length >= 1 && args[0] == "--external-process-fixture") return RunExternalProcessFixture(args);
if (args.Length > 1 || (args.Length == 1 && args[0] != "--unit"))
{
    Console.Error.WriteLine("使用方法: Ateliers.Ai.Orchestrator.Tests [--unit]");
    return 2;
}

var suite = new TestSuite();
AddUnitTests(suite);
return suite.Run().ExitCode;

static void AddUnitTests(TestSuite suite)
{
    // CI invokes this explicit Fake/fixture-only set. External-service and Docker tests do not belong here.
    suite.Add("テスト集の件数・ログ・終了コードがPass/Fail/未検証で一致する", TestSuiteOutcomeContractIsConsistent);
    suite.Add("有効な契約を検証できる", ValidContractIsAccepted);
    suite.Add("契約の不足・矛盾・未知項目を拒否する", InvalidContractsAreRejected);
    suite.Add("すべての状態ペアを許可表どおりに判定する", AllTransitionsAreExplicit);
    suite.Add("確認待ちはIssue #3では到達不能", ReviewReadyIsUnavailableUntilEvidenceIntegration);
    suite.Add("JSONLイベントに連番とUTC時刻を記録する", EventLogIsSequencedAndUtc);
    suite.Add("禁止遷移は状態と記録を変えない", RejectedTransitionDoesNotMutate);
    suite.Add("Fake正常系は成果物固定後にレビューし証跡不足で停止する", FakeSuccessKeepsReviewAndReadyEvidenceSeparate);
    suite.Add("Fake修正系は同一Reviewerで再レビューし上限を守る", FakeRevisionIsBoundedAndReReviewed);
    suite.Add("Fake停止・失敗・空応答・タイムアウトを正しく報告する", FakeStopAndFailureScenariosAreRecorded);
    suite.Add("再試行上限でReviewer失敗を終了する", ReviewerRetriesStopAtLimit);
    suite.Add("修正上限到達時に人間へ停止を報告する", RevisionLimitStopsForHuman);
    suite.Add("イベント保存失敗時に成功状態へ進まず部分成果物を保持する", EventWriteFailureDoesNotClaimSuccess);
    suite.Add("停止イベント・報告保存失敗を実JSONLと結果へ反映する", ReportPersistenceFailuresAreConsistent);
    suite.Add("report_written記録失敗後に保存済み報告を更新する", ReportWrittenEventFailureUpdatesReport);
    suite.Add("Ready遷移後のreport_written失敗を報告状態へ反映する", ReadyReportWrittenEventFailureUpdatesState);
    suite.Add("Fake成果物保存失敗はReviewerを起動せず停止理由を記録する", FakeArtifactWriteFailureStopsAndReports);
    suite.Add("Fake Reviewer応答の途中保存失敗は追加Agentなしで停止する", FakeMidwayWriteFailureStopsAndReports);
    suite.Add("既存実行保存先を上書きせずに拒否する", ExistingRunDirectoryIsPreserved);
    suite.Add("人間確認待ちは独立証跡とPR操作記録が揃わなければ拒否する", ReadyRequiresIndependentEvidenceRecords);
    suite.Add("空のrunパス指定を終了コード2の入力エラーとして返す", EmptyRunPathsReturnInputError);
    suite.Add("証跡ファイルの読込・JSON形式エラーを実行前に拒否する", ReviewEvidenceReadErrorsAreInputFailures);
    suite.Add("NUL文字を含む外部証跡参照を不一致として拒否する", InvalidEvidenceReferenceIsRejected);
    suite.Add("Fake Codex出力の既知プロキシ認証情報を保存前にマスクする", CodexProxyCredentialsAreRedactedFromPersistedOutputs);
    suite.Add("Fake CodexプロセスのJSONL・stderr・usage・未知eventを別々に記録する", CodexProcessCapturesJsonlAndSeparatesStreams);
    suite.Add("Fake Codex実行でkill処理とCLI版確認が起動しない", CodexVersionProbeKillFailureIsRecorded);
    suite.Add("Fake Codex実行ファイル起動失敗を安全に記録する", CodexVersionProbeStartFailureStopsBeforeExecution);
    suite.Add("terminal eventと終了コードの不一致を成功扱いしない", CodexTerminalAndExitCodeMustAgree);
    suite.Add("不正JSONL・空応答・異常終了を成功扱いしない", CodexMalformedAndEmptyResponsesFail);
    suite.Add("timeout後にkillを試し終了・stream切断を記録する", CodexTimeoutKillIsReportedSeparately);
    suite.Add("timeout時にFakeプロセスの子プロセス停止も確認する", CodexTimeoutStopsFakeChildProcess);
    suite.Add("kill失敗を記録しプロセス終了未確認を成功扱いしない", CodexKillFailureIsNotReportedAsSuccess);
    suite.Add("Ctrl+C停止要求とtimeoutを区別しkill・終了・stream結果を記録する", CodexCancellationIsDistinctFromTimeout);
    suite.Add("Codex子プロセスへ親のAPI secret環境変数を渡さない", CodexChildEnvironmentFiltersSecrets);
    suite.Add("既存CODEX_HOMEと通常のユーザープロファイルを使える", CodexExistingProfilesAreAccepted);
    suite.Add("Codex Fakeプロセスで独立レビューまで縦断し固定hashを引き継ぐ", CodexFakeProcessesCompleteIndependentReviewSlice);
    suite.Add("Codex一回モードは成功・指摘・失敗を記録し追加起動せず人間判断待ちで止まる", CodexOneShotStopsWithoutRetries);
}

static int RunFakeCodexFixture(string[] args)
{
    Console.InputEncoding = new System.Text.UTF8Encoding(false);
    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
    var mode = args.Length > 1 ? args[1] : "success";
    if (mode == "child-sleeper") { Thread.Sleep(10_000); return 0; }
    if (args[^1] == "--version")
    {
        if (mode == "version-timeout") Thread.Sleep(6_000);
        Console.WriteLine("codex-cli fixture-1");
        return 0;
    }
    var prompt = Console.In.ReadToEnd();
    if (prompt.Contains("ROLE=IMPLEMENTER", StringComparison.Ordinal) || prompt.Contains("ROLE=REVIEWER", StringComparison.Ordinal))
    {
        var policy = new
        {
            scope_included_present = prompt.Contains("\"Included\"", StringComparison.Ordinal),
            scope_excluded_present = prompt.Contains("\"Excluded\"", StringComparison.Ordinal),
            os_boundary_disclaimed = prompt.Contains("作業範囲はAgentへの指示であり、OSレベルで範囲外アクセスを完全に防ぐ保証ではありません。", StringComparison.Ordinal)
        };
        Console.WriteLine($"{{\"type\":\"fixture.prompt_policy\",\"policy\":{JsonSerializer.Serialize(policy)}}}");
    }
    Console.Error.WriteLine("fixture warning: stderr is independent from the terminal status");
    var proxy = new[] { "HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY", "https_proxy", "http_proxy", "all_proxy" }
        .Select(Environment.GetEnvironmentVariable).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    if (mode == "proxy-output")
    {
        Console.WriteLine("{\"type\":\"fixture.proxy_username\",\"username\":\"split-proxy-user\"}");
        Console.Error.WriteLine("proxy diagnostic password: split-proxy-password");
        Console.WriteLine($"{{\"type\":\"fixture.proxy_diagnostic\",\"message\":{JsonSerializer.Serialize($"proxy diagnostic URL: {proxy}")}}}");
    }
    if (mode == "timeout-with-child")
    {
        var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            ArgumentList = { "--fake-codex-fixture", "child-sleeper" }
        }) ?? throw new InvalidOperationException("fixture child process did not start");
        Console.WriteLine($"{{\"type\":\"fixture.child\",\"process_id\":{child.Id}}}");
        Console.Out.Flush();
        Thread.Sleep(10_000);
        return 0;
    }
    if (mode == "timeout") { Thread.Sleep(5_000); return 0; }
    if (mode == "cancel") { Thread.Sleep(5_000); return 0; }
    if (mode == "kill-failure") { Thread.Sleep(500); return 0; }
    if (prompt.Contains("ROLE=IMPLEMENTER", StringComparison.Ordinal)) File.WriteAllText("example.txt", "fixture change\n");
    if (prompt.Contains("ROLE=REVIEWER", StringComparison.Ordinal) && mode is "workflow" or "artifact-only")
    {
        File.WriteAllText("review-notes.txt", "fixture supplemental artifact");
        Directory.CreateDirectory("analysis");
        File.WriteAllText(Path.Combine("analysis", "proposal.json"), "{\"proposal\":true}");
    }
    Console.WriteLine($"{{\"type\":\"fixture.arguments\",\"arguments\":{JsonSerializer.Serialize(args)}}}");
    Console.WriteLine($"{{\"type\":\"fixture.environment\",\"openai_key_present\":{(Environment.GetEnvironmentVariable("OPENAI_API_KEY") is null ? "false" : "true")},\"https_proxy_present\":{(Environment.GetEnvironmentVariable("HTTPS_PROXY") is null ? "false" : "true")}}}");
    var environmentPaths = new Dictionary<string, string?>
    {
        ["CODEX_HOME"] = Environment.GetEnvironmentVariable("CODEX_HOME"),
        ["HOME"] = Environment.GetEnvironmentVariable("HOME"),
        ["USERPROFILE"] = Environment.GetEnvironmentVariable("USERPROFILE"),
        ["APPDATA"] = Environment.GetEnvironmentVariable("APPDATA"),
        ["LOCALAPPDATA"] = Environment.GetEnvironmentVariable("LOCALAPPDATA"),
        ["TEMP"] = Environment.GetEnvironmentVariable("TEMP"),
        ["TMP"] = Environment.GetEnvironmentVariable("TMP"),
        ["TMPDIR"] = Environment.GetEnvironmentVariable("TMPDIR")
    };
    Console.WriteLine($"{{\"type\":\"fixture.environment_paths\",\"paths\":{JsonSerializer.Serialize(environmentPaths)}}}");
    if (prompt.Contains("ROLE=REVIEWER", StringComparison.Ordinal))
        Console.WriteLine($"{{\"type\":\"fixture.reviewer_input\",\"self_assessment_present\":{prompt.Contains("IMPLEMENTER-SELF-ASSESSMENT-SECRET", StringComparison.Ordinal).ToString().ToLowerInvariant()}}}");
    if (mode == "malformed") Console.WriteLine("{not-json");
    var threadId = prompt.Contains("ROLE=REVIEWER", StringComparison.Ordinal)
        ? prompt.Contains("REVIEW_ATTEMPT: 1", StringComparison.Ordinal) ? "thread-fixture-reviewer-1" : "thread-fixture-reviewer-2"
        : "thread-fixture-implementer";
    Console.WriteLine($"{{\"type\":\"thread.started\",\"thread_id\":\"{threadId}\"}}");
    Console.WriteLine("{\"type\":\"future.unknown\",\"extra\":true}");
    if (mode != "empty" && !(mode == "artifact-only" && prompt.Contains("ROLE=REVIEWER", StringComparison.Ordinal)))
    {
        var message = mode == "proxy-output" && prompt.Contains("ROLE=REVIEWER", StringComparison.Ordinal)
            ? "レビュー合格: proxy credentials split-proxy-user / split-proxy-password"
            : mode == "proxy-output" ? "proxy credentials split-proxy-user / split-proxy-password"
            : mode == "workflow" && prompt.Contains("ROLE=REVIEWER", StringComparison.Ordinal)
            ? prompt.Contains("REVIEW_ATTEMPT: 1", StringComparison.Ordinal) ? "指摘: テスト結果を明記してください。" : "レビュー合格: fixture reviewed fixed artifact"
            : (mode is "workflow" or "artifact-only") && prompt.Contains("ROLE=IMPLEMENTER", StringComparison.Ordinal)
                ? "IMPLEMENTER-SELF-ASSESSMENT-SECRET" : "fixture response";
        var serializedMessage = JsonSerializer.Serialize(message);
        Console.WriteLine($"{{\"type\":\"item.completed\",\"item\":{{\"type\":\"agent_message\",\"text\":{serializedMessage}}}}}");
    }
    Console.WriteLine("{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":12,\"output_tokens\":3}}");
    return mode == "exit-mismatch" ? 7 : 0;
}

static int RunExternalProcessFixture(string[] args)
{
    var mode = args.Length > 1 ? args[1] : "success";
    if (mode == "sleep") { Thread.Sleep(5_000); return 0; }
    Console.WriteLine("C:\\Users\\Local User\\private-output.txt /home/local-user/private-output.txt");
    Console.Error.WriteLine("C:\\Users\\Local User\\private-error.txt /home/local-user/private-error.txt");
    return mode == "fail" ? 7 : 0;
}

static void CodexProxyCredentialsAreRedactedFromPersistedOutputs()
{
    const string username = "split-proxy-user";
    const string password = "split-proxy-password";
    var proxyVariables = new[] { "HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY", "https_proxy", "http_proxy", "all_proxy" };
    var originalValues = proxyVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
    try
    {
        foreach (var name in proxyVariables)
        {
            foreach (var proxyVariable in proxyVariables) Environment.SetEnvironmentVariable(proxyVariable, null);
            var proxyUrl = $"http://{username}:{password}@{name.ToLowerInvariant()}.proxy.example.invalid:8080";
            Environment.SetEnvironmentVariable(name, proxyUrl);
            var (runner, request, root) = CreateCodexFixture("proxy-output");
            using (root)
            {
                var eventPath = Path.Combine(request.RunDirectory!, "events.jsonl");
                FakeWorkflowResult result;
                using (var events = JsonlRunEventStore.CreateNew(eventPath, request.RunId))
                    result = new FakeAgentWorkflow().Run(request.Contract, new string('d', 64), request.RunId,
                        request.RunDirectory!, runner, events, codexOneShot: true);

                Assert.Equal(RunState.NeedsHumanInput, result.State);
                var persistedFiles = Directory.EnumerateFiles(request.RunDirectory!, "*", SearchOption.AllDirectories);
                foreach (var file in persistedFiles)
                {
                    var contents = File.ReadAllText(file);
                    Assert.False(contents.Contains(username, StringComparison.Ordinal), $"ユーザー名が記録に残っています ({name}): {Path.GetFileName(file)}");
                    Assert.False(contents.Contains(password, StringComparison.Ordinal), $"パスワードが記録に残っています ({name}): {Path.GetFileName(file)}");
                    Assert.False(contents.Contains(proxyUrl, StringComparison.Ordinal), $"プロキシURLが記録に残っています ({name}): {Path.GetFileName(file)}");
                }
                var stdout = File.ReadAllText(Path.Combine(request.RunDirectory!, "exchanges", "implementer-1", "codex.stdout.jsonl"));
                var stderr = File.ReadAllText(Path.Combine(request.RunDirectory!, "exchanges", "implementer-1", "codex.stderr.txt"));
                Assert.True(stdout.Contains("[REDACTED_PROXY]", StringComparison.Ordinal));
                Assert.True(stderr.Contains("[REDACTED_PROXY]", StringComparison.Ordinal));
                var selfAssessmentPath = Path.Combine(request.RunDirectory!, "exchanges", "implementer-1", "self-assessment.md");
                Assert.True(File.Exists(selfAssessmentPath), $"Implementer自己評価が保存されていません ({name}): {File.ReadAllText(Path.Combine(request.RunDirectory!, result.ReportReference))}");
                var selfAssessment = File.ReadAllText(selfAssessmentPath);
                Assert.True(selfAssessment.Contains("[REDACTED_PROXY]", StringComparison.Ordinal));
                Assert.True(File.Exists(Path.Combine(request.RunDirectory!, result.ReportReference)));
            }
        }
    }
    finally
    {
        foreach (var (name, value) in originalValues) Environment.SetEnvironmentVariable(name, value);
    }
}

static void CodexProcessCapturesJsonlAndSeparatesStreams()
{
    var (runner, request, root) = CreateCodexFixture("success");
    using (root)
    {
        var response = runner.Implement(request);
        Assert.True(response.Succeeded, response.ExecutionEvidence?.Error ?? "正常terminal eventと終了コード0を成功扱いしませんでした。");
        Assert.True(response.Content.Contains("fixture change", StringComparison.Ordinal), "Git作業領域の実差分が固定成果物に含まれていません。");
        Assert.False(response.Content.Contains("fixture response", StringComparison.Ordinal), "Implementerの自己申告がReviewer向け固定成果物へ混入しました。");
        Assert.Equal("fixture response", response.SelfAssessment);
        Assert.Equal("thread-fixture-implementer", response.ExecutionEvidence!.ThreadId);
        Assert.Equal("turn.completed", response.ExecutionEvidence.TerminalEvent);
        Assert.True(response.ExecutionEvidence.UsageJson!.Contains("input_tokens", StringComparison.Ordinal));
        var rawEvents = File.ReadAllText(Path.Combine(request.RunDirectory!, response.ExecutionEvidence.StandardOutputReference!));
        Assert.True(rawEvents.Contains("future.unknown", StringComparison.Ordinal));
        Assert.True(rawEvents.Contains("never", StringComparison.Ordinal), "非対話プロセスのapproval policy引数がありません。");
        Assert.False(rawEvents.Contains("--ephemeral", StringComparison.Ordinal));
        Assert.True(File.ReadAllText(Path.Combine(request.RunDirectory!, response.ExecutionEvidence.StandardErrorReference!)).Contains("fixture warning", StringComparison.Ordinal));
        Assert.True(response.ExecutionEvidence.CliVersion is null, "PoCで取得していないCLI版が値を持っています。");
        Assert.False(Directory.EnumerateFiles(Path.Combine(request.RunDirectory!, "exchanges", "implementer-1"), "*version*", SearchOption.AllDirectories).Any(),
            "余分なCLI版確認プロセスの証跡が作られています。");
        Assert.False(rawEvents.Contains("--version", StringComparison.Ordinal), "codex --version が起動引数に含まれています。");
        Assert.True(response.ExecutionEvidence.ProcessId > 0);
        Assert.True(response.ExecutionEvidence.ProcessExitObserved == true && response.ExecutionEvidence.StreamsClosed == true);
    }
}

static void CodexVersionProbeKillFailureIsRecorded()
{
    var killCalls = 0;
    var (runner, request, root) = CreateCodexFixture("success", killOverride: _ =>
    { killCalls++; throw new InvalidOperationException("unexpected kill invocation"); });
    using (root)
    {
        var response = runner.Implement(request);
        Assert.True(response.Succeeded, response.ExecutionEvidence?.Error ?? "Fake Agent runが成功しませんでした。");
        Assert.Equal(0, killCalls, "通常終了時に停止処理が呼び出されました。");
        Assert.True(response.ExecutionEvidence!.CliVersion is null);
        Assert.False(Directory.EnumerateFiles(Path.Combine(request.RunDirectory!, "exchanges"), "*version*", SearchOption.AllDirectories).Any());
    }
}

static void CodexVersionProbeStartFailureStopsBeforeExecution()
{
    using var root = new TemporaryDirectory();
    var implementer = Path.Combine(root.Path, "implementer-profile");
    var reviewer = Path.Combine(root.Path, "reviewer-profile");
    foreach (var home in new[] { implementer, reviewer })
        Directory.CreateDirectory(home);
    var invalidExecutable = Path.Combine(root.Path, "not-an-executable.exe");
    File.WriteAllText(invalidExecutable, "fixture only");
    var workspace = Path.Combine(Environment.CurrentDirectory, ".test-workspaces", Guid.NewGuid().ToString("N"));
    root.AdditionalPath = workspace;
    var runDirectory = Path.Combine(root.Path, "run");
    Directory.CreateDirectory(workspace);
    Directory.CreateDirectory(runDirectory);
    File.WriteAllText(Path.Combine(workspace, "example.txt"), "baseline\n");
    RunGitFixture(workspace, "init", "-q");
    RunGitFixture(workspace, "add", "example.txt");
    RunGitFixture(workspace, "-c", "user.name=Orchestrator Test", "-c", "user.email=orchestrator-test@example.invalid", "commit", "-qm", "baseline");
    var options = new CodexCliRunnerOptions(invalidExecutable, implementer, reviewer);
    var request = new AgentRequest("fixture-run", "Implementer", SampleContract() with { WorkingDirectory = workspace }, 1,
        RunDirectory: runDirectory, RemainingTime: TimeSpan.FromSeconds(5));

    var response = new CodexCliAgentRunner(options).Implement(request);
    Assert.False(response.Succeeded);
    Assert.True(response.ExecutionEvidence!.ProcessId is null,
        "起動に失敗したCLIのprocess IDが記録されました。");
    Assert.True(response.ExecutionEvidence.Error is not null,
        "Fake CLI実行ファイルの起動エラーが記録されていません。");
    Assert.True(response.ExecutionEvidence.CliVersion is null);
}

static void CodexTerminalAndExitCodeMustAgree()
{
    var (runner, request, root) = CreateCodexFixture("exit-mismatch");
    using (root)
    {
        var response = runner.Implement(request);
        Assert.False(response.Succeeded);
        Assert.Equal(7, response.ExecutionEvidence!.ExitCode);
        Assert.Equal("turn.completed", response.ExecutionEvidence.TerminalEvent);
    }
}

static void CodexMalformedAndEmptyResponsesFail()
{
    foreach (var scenario in new[] { "malformed", "empty" })
    {
        var (runner, request, root) = CreateCodexFixture(scenario);
        using (root)
        {
            var response = runner.Implement(request);
            Assert.False(response.Succeeded, $"{scenario} が成功扱いされました。");
            Assert.True(File.Exists(Path.Combine(request.RunDirectory!, response.ExecutionEvidence!.StandardOutputReference!)));
        }
    }
}

static void CodexTimeoutKillIsReportedSeparately()
{
    var (runner, request, root) = CreateCodexFixture("timeout", TimeSpan.FromMilliseconds(100));
    using (root)
    {
        var response = runner.Implement(request);
        Assert.False(response.Succeeded);
        Assert.True(response.TimedOut);
        Assert.True(response.ExecutionEvidence!.KillAttempted);
        Assert.True(response.ExecutionEvidence.ProcessExitObserved == true);
        Assert.True(response.ExecutionEvidence.StreamsClosed == true);
    }
}

static void CodexTimeoutStopsFakeChildProcess()
{
    var (runner, request, root) = CreateCodexFixture("timeout-with-child", TimeSpan.FromMilliseconds(200));
    using (root)
    {
        var response = runner.Implement(request);
        Assert.False(response.Succeeded);
        Assert.True(response.ExecutionEvidence!.TimedOut);
        Assert.True(response.ExecutionEvidence.KillAttempted);
        Assert.True(response.ExecutionEvidence.ProcessExitObserved == true);
        var stdoutPath = Path.Combine(request.RunDirectory!, response.ExecutionEvidence.StandardOutputReference!);
        var childLine = File.ReadLines(stdoutPath).FirstOrDefault(line => line.Contains("fixture.child", StringComparison.Ordinal));
        Assert.True(childLine is not null, "Fake child processのPID証跡がありません。");
        using var childEvent = JsonDocument.Parse(childLine!);
        var childId = childEvent.RootElement.GetProperty("process_id").GetInt32();
        var exited = SpinWait.SpinUntil(() =>
        {
            try { using var child = Process.GetProcessById(childId); return child.HasExited; }
            catch (ArgumentException) { return true; }
            catch (InvalidOperationException) { return true; }
        }, TimeSpan.FromSeconds(5));
        Assert.True(exited, $"timeout後もFake child process {childId}の終了を確認できませんでした。");
    }
}

static void CodexKillFailureIsNotReportedAsSuccess()
{
    var (runner, request, root) = CreateCodexFixture("kill-failure", TimeSpan.FromMilliseconds(50),
        _ => throw new InvalidOperationException("fixture kill failure"));
    using (root)
    {
        var response = runner.Implement(request);
        Assert.False(response.Succeeded);
        Assert.True(response.ExecutionEvidence!.KillAttempted);
        Assert.True(response.ExecutionEvidence.Error!.Contains("fixture kill failure", StringComparison.Ordinal));
        Assert.True(response.ExecutionEvidence.ProcessExitObserved == true);
    }
}

static void CodexCancellationIsDistinctFromTimeout()
{
    using var cancellation = new CancellationTokenSource();
    var (runner, request, root) = CreateCodexFixture("cancel", cancellationToken: cancellation.Token);
    using (root)
    {
        var task = Task.Run(() => runner.Implement(request));
        Thread.Sleep(150);
        cancellation.Cancel();
        var response = task.GetAwaiter().GetResult();
        Assert.False(response.Succeeded);
        Assert.True(response.StopRequested);
        Assert.False(response.TimedOut);
        Assert.Equal("caller-cancellation", response.ExecutionEvidence!.StopReason);
        Assert.True(response.ExecutionEvidence.KillAttempted);
        Assert.True(response.ExecutionEvidence.ProcessExitObserved == true);
        Assert.True(response.ExecutionEvidence.StreamsClosed == true);
    }
}

static void CodexChildEnvironmentFiltersSecrets()
{
    var original = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    var originalProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY");
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", "fixture-secret-do-not-forward");
    Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://fixture-proxy.invalid:8080");
    var (runner, request, root) = CreateCodexFixture("success");
    try
    {
        using (root)
        {
            var response = runner.Implement(request);
            Assert.True(response.Succeeded);
            var rawEvents = File.ReadAllText(Path.Combine(request.RunDirectory!, response.ExecutionEvidence!.StandardOutputReference!));
            Assert.True(rawEvents.Contains("\"openai_key_present\":false", StringComparison.Ordinal), "親環境のAPI keyがFake childへ継承されました。");
            Assert.True(rawEvents.Contains("\"https_proxy_present\":true", StringComparison.Ordinal), "親環境のHTTPS proxy設定がFake childへ継承されませんでした。");
        }
    }
    finally
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", original);
        Environment.SetEnvironmentVariable("HTTPS_PROXY", originalProxy);
    }
}

static void CodexExistingProfilesAreAccepted()
{
    using var root = new TemporaryDirectory();
    var existingHome = Path.Combine(root.Path, "existing-codex-home");
    Directory.CreateDirectory(existingHome);
    File.WriteAllText(Path.Combine(existingHome, "auth.json"), "fixture credentials are not used by this test");

    var options = new CodexCliRunnerOptions(Environment.ProcessPath!, existingHome, existingHome,
        ["--fake-codex-fixture", "success"]).Validate();
    Assert.True(options is not null, "既存のCODEX_HOMEを使用できませんでした。");

    var original = Environment.GetEnvironmentVariable("CODEX_HOME");
    try
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", existingHome);
        Assert.Equal(Path.GetFullPath(existingHome), CodexCliRunnerOptions.ResolveCodexHome(null));
    }
    finally { Environment.SetEnvironmentVariable("CODEX_HOME", original); }
}

static void CodexFakeProcessesCompleteIndependentReviewSlice()
{
    var (runner, request, root) = CreateCodexFixture("artifact-only");
    using (root)
    {
        var eventPath = Path.Combine(request.RunDirectory!, "events.jsonl");
        FakeWorkflowResult result;
        using (var events = JsonlRunEventStore.CreateNew(eventPath, request.RunId))
            result = new FakeAgentWorkflow().Run(request.Contract, new string('d', 64), request.RunId,
                request.RunDirectory!, runner, events, codexOneShot: true);
        Assert.Equal(RunState.NeedsHumanInput, result.State, "レビュー合格のみでPR確認待ちに進みました。");
        Assert.Equal(0, result.RevisionAttempts);
        Assert.Equal(1, result.ImplementationCalls);
        Assert.Equal(1, result.ReviewCalls);
        var firstReviewRequest = File.ReadAllText(Path.Combine(request.RunDirectory!, "exchanges", "reviewer-1", "request.json"));
        Assert.True(firstReviewRequest.Contains(result.ArtifactSha256!, StringComparison.Ordinal), "レビュー要求に固定成果物ハッシュがありません。");
        Assert.False(firstReviewRequest.Contains("自己評価", StringComparison.Ordinal));
        var firstReviewEvidence = JsonDocument.Parse(File.ReadAllText(Path.Combine(request.RunDirectory!, "exchanges", "reviewer-1", "execution-evidence.json")));
        Assert.Equal("thread-fixture-reviewer-1", firstReviewEvidence.RootElement.GetProperty("ThreadId").GetString());
        Assert.Equal(result.ArtifactSha256, firstReviewEvidence.RootElement.GetProperty("ArtifactSha256").GetString());
        var report = File.ReadAllText(Path.Combine(request.RunDirectory!, result.ReportReference));
        Assert.True(report.Contains("thread-fixture-implementer", StringComparison.Ordinal), "レポートにImplementer実行識別子がありません。");
        Assert.True(report.Contains("thread-fixture-reviewer-1", StringComparison.Ordinal), "レポートにReviewer実行識別子がありません。");
        Assert.False(report.Contains("thread-fixture-reviewer-2", StringComparison.Ordinal), "Reviewerが二回起動しています。");
        Assert.True(report.Contains("CLI=未取得", StringComparison.Ordinal), "レポートにCLI版の未取得状態がありません。");
        Assert.True(report.Contains("実費・請求額・硬い予算上限: 未取得", StringComparison.Ordinal), "レポートに費用情報の未取得状態がありません。");
        Assert.True(File.Exists(Path.Combine(request.RunDirectory!, "exchanges", "implementer-1", "self-assessment.md")), "Implementerの自己評価記録がありません。");
        Assert.True(File.ReadAllText(Path.Combine(request.RunDirectory!, "exchanges", "implementer-1", "self-assessment.md"))
            .Contains("IMPLEMENTER-SELF-ASSESSMENT-SECRET", StringComparison.Ordinal));
        var implementerStdout = File.ReadAllText(Path.Combine(request.RunDirectory!, "exchanges", "implementer-1", "codex.stdout.jsonl"));
        Assert.False(implementerStdout.Contains("--version", StringComparison.Ordinal), "Codex版確認プロセスが起動しています。");
        AssertCodexFixturePolicy(implementerStdout, "workspace-write", "Implementer");
        var firstReviewStdoutPath = Path.Combine(request.RunDirectory!, "exchanges", "reviewer-1", "codex.stdout.jsonl");
        var firstReviewStdout = File.ReadAllText(firstReviewStdoutPath);
        Assert.True(firstReviewStdout.Contains("\"self_assessment_present\":false", StringComparison.Ordinal),
            "ReviewerプロンプトにImplementerの自己評価が混入しました。");
        AssertCodexFixturePolicy(firstReviewStdout, "workspace-write", "Reviewer");
        var reviewerArtifact = Path.Combine(request.RunDirectory!, "exchanges", "reviewer-1", "artifacts", "review-notes.txt");
        Assert.True(File.Exists(reviewerArtifact), "Reviewerが生成した任意成果物が保持されていません。");
        Assert.Equal("fixture supplemental artifact", File.ReadAllText(reviewerArtifact));
        var nestedReviewerArtifact = Path.Combine(request.RunDirectory!, "exchanges", "reviewer-1", "artifacts", "analysis", "proposal.json");
        Assert.Equal("{\"proposal\":true}", File.ReadAllText(nestedReviewerArtifact));
        Assert.False(Path.GetFullPath(reviewerArtifact).StartsWith(
            Path.GetFullPath(request.Contract.WorkingDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Reviewer成果物出力先がレビュー対象の作業領域内にあります。");
        var reviewExecutionEvidence = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(request.RunDirectory!, "exchanges", "reviewer-1", "execution-evidence.json")));
        Assert.Equal(Path.GetDirectoryName(reviewerArtifact), reviewExecutionEvidence.RootElement.GetProperty("WorkingDirectory").GetString());
        var reviewCallEvent = JsonDocument.Parse(File.ReadAllLines(eventPath)
            .First(line => line.Contains("\"role\":\"Reviewer\"", StringComparison.Ordinal) &&
                line.Contains("\"eventType\":\"agent_call\"", StringComparison.Ordinal)));
        var artifactReferences = reviewCallEvent.RootElement.GetProperty("artifactReferences").EnumerateArray()
            .Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.True(artifactReferences.SetEquals([
            "exchanges/reviewer-1/artifacts/review-notes.txt",
            "exchanges/reviewer-1/artifacts/analysis/proposal.json"]), "任意成果物の参照がすべてイベントに記録されていません。");
        Assert.False(File.Exists(Path.Combine(request.RunDirectory!, "exchanges", "reviewer-1", "response.md")),
            "本文を返さないReviewerに固定形式のresponse.mdを要求しました。");
        var implementerPaths = ReadFakeEnvironmentPaths(Path.Combine(request.RunDirectory!, "exchanges", "implementer-1", "codex.stdout.jsonl"));
        var reviewerPaths = ReadFakeEnvironmentPaths(firstReviewStdoutPath);
        Assert.Equal(Path.Combine(root.Path, "implementer-profile"), implementerPaths["CODEX_HOME"]);
        Assert.Equal(Path.Combine(root.Path, "reviewer-profile"), reviewerPaths["CODEX_HOME"]);
        foreach (var name in new[] { "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "TMPDIR" })
        {
            var inherited = Environment.GetEnvironmentVariable(name);
            Assert.Equal(inherited, implementerPaths[name], $"{name}を既存ユーザー環境から変更しました。");
            Assert.Equal(inherited, reviewerPaths[name], $"{name}を既存ユーザー環境から変更しました。");
        }
    }
}

static void AssertCodexFixturePolicy(string stdout, string expectedSandbox, string role)
{
    var policyLine = stdout.Split('\n').First(line => line.Contains("fixture.prompt_policy", StringComparison.Ordinal));
    using var policyEvent = JsonDocument.Parse(policyLine);
    var policy = policyEvent.RootElement.GetProperty("policy");
    Assert.True(policy.GetProperty("scope_included_present").GetBoolean(), "Agent要求に含める作業範囲がありません。");
    Assert.True(policy.GetProperty("scope_excluded_present").GetBoolean(), "Agent要求に除外範囲がありません。");
    Assert.True(policy.GetProperty("os_boundary_disclaimed").GetBoolean(), $"{role}要求にOS境界の説明がありません: {policyEvent.RootElement.GetRawText()}");

    var argumentsLine = stdout.Split('\n').First(line => line.Contains("fixture.arguments", StringComparison.Ordinal));
    using var argumentsEvent = JsonDocument.Parse(argumentsLine);
    var arguments = argumentsEvent.RootElement.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()).ToArray();
    var sandboxIndex = Array.IndexOf(arguments, "--sandbox");
    Assert.True(sandboxIndex >= 0 && sandboxIndex + 1 < arguments.Length && arguments[sandboxIndex + 1] == expectedSandbox,
        $"Codex sandboxに {expectedSandbox} を指定していません。");
    var approvalConfigIndex = Array.IndexOf(arguments, "--config");
    Assert.True(approvalConfigIndex >= 0 && approvalConfigIndex + 1 < arguments.Length &&
        arguments[approvalConfigIndex + 1] == "approval_policy=\"never\"",
        "非対話Codex runの承認設定が確認できません。");
}

static Dictionary<string, string> ReadFakeEnvironmentPaths(string stdoutPath)
{
    foreach (var line in File.ReadLines(stdoutPath))
    {
        using var document = JsonDocument.Parse(line);
        if (document.RootElement.TryGetProperty("type", out var type) && type.GetString() == "fixture.environment_paths")
            return document.RootElement.GetProperty("paths").EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
    }
    throw new Exception("Fake childの実効profile環境を取得できませんでした。");
}

static (CodexCliAgentRunner Runner, AgentRequest Request, TemporaryDirectory Root) CreateCodexFixture(
    string scenario, TimeSpan? timeout = null, Action<Process>? killOverride = null, CancellationToken cancellationToken = default)
{
    var root = new TemporaryDirectory();
    var runDirectory = Path.Combine(root.Path, "run");
    var workspace = Path.Combine(Environment.CurrentDirectory, ".test-workspaces", Guid.NewGuid().ToString("N"));
    root.AdditionalPath = workspace;
    var implementerHome = Path.Combine(root.Path, "implementer-profile");
    var reviewerHome = Path.Combine(root.Path, "reviewer-profile");
    Directory.CreateDirectory(runDirectory);
    Directory.CreateDirectory(workspace);
    File.WriteAllText(Path.Combine(workspace, "example.txt"), "baseline\n");
    RunGitFixture(workspace, "init", "-q");
    RunGitFixture(workspace, "add", "example.txt");
    RunGitFixture(workspace, "-c", "user.name=Orchestrator Test", "-c", "user.email=orchestrator-test@example.invalid", "commit", "-qm", "baseline");
    foreach (var home in new[] { implementerHome, reviewerHome })
        Directory.CreateDirectory(home);
    var executable = Environment.ProcessPath!;
    var options = new CodexCliRunnerOptions(executable, implementerHome, reviewerHome,
        ["--fake-codex-fixture", scenario], killOverride, cancellationToken);
    var request = new AgentRequest("fixture-run", "Implementer", SampleContract() with { WorkingDirectory = workspace }, 1,
        RunDirectory: runDirectory, RemainingTime: timeout ?? TimeSpan.FromSeconds(5));
    return (new CodexCliAgentRunner(options), request, root);
}

static void RunGitFixture(string workingDirectory, params string[] arguments)
{
    var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, UseShellExecute = false,
        RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new Exception("Git fixture process did not start.");
    process.WaitForExit();
    if (process.ExitCode != 0) throw new Exception($"Git fixture failed: {process.StandardError.ReadToEnd()}");
}

static void ValidContractIsAccepted()
{
    var errors = TaskContractReader.Validate(SampleContract());
    Assert.Equal(0, errors.Count, string.Join(Environment.NewLine, errors));

    using var temp = new TemporaryDirectory();
    var path = Path.Combine(temp.Path, "contract.json");
    File.WriteAllText(path, JsonSerializer.Serialize(SampleContract(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    var loaded = TaskContractReader.Read(path);
    Assert.True(loaded.IsValid, string.Join(Environment.NewLine, loaded.Errors));
    Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), loaded.ContentSha256);
}

static void InvalidContractsAreRejected()
{
    var invalid = SampleContract() with
    {
        SchemaVersion = 2,
        TaskId = "bad\ntask",
        WorkingDirectory = "relative/path",
        Scope = new TaskScope { Included = ["src"], Excluded = ["SRC"] },
        Requirements = [],
        Limits = new ExecutionLimits { MaxDurationMinutes = 0, MaxRevisionAttempts = -1, MaxRetries = -1 }
    };
    var errors = TaskContractReader.Validate(invalid);
    Assert.True(errors.Count >= 6, "不足・不正な条件がすべて検出されていません。");

    using var temp = new TemporaryDirectory();
    var path = Path.Combine(temp.Path, "unknown.json");
    File.WriteAllText(path, "{\"schemaVersion\":1,\"unknown\":true}");
    Assert.False(TaskContractReader.Read(path).IsValid, "未知のJSON項目を拒否できませんでした。");

    Assert.True(TaskContractReader.Validate(SampleContract() with { WorkingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) })
        .Any(error => error.Contains("作業ディレクトリが存在しません", StringComparison.Ordinal)), "存在しない作業ディレクトリを拒否できませんでした。");
}

static void AllTransitionsAreExplicit()
{
    var states = Enum.GetValues<RunState>();
    foreach (var from in states)
    foreach (var to in states)
    {
        var shouldAllow = (from, to) switch
        {
            (RunState.Prepared, RunState.Implementing or RunState.NeedsHumanInput or RunState.Failed) => true,
            (RunState.Implementing, RunState.Reviewing or RunState.NeedsHumanInput or RunState.Failed) => true,
            (RunState.Reviewing, RunState.Revising or RunState.NeedsHumanInput or RunState.Failed) => true,
            (RunState.Revising, RunState.Implementing or RunState.NeedsHumanInput or RunState.Failed) => true,
            _ => false
        };

        if (shouldAllow) RunStateMachine.EnsureAllowed(from, to);
        else Assert.Throws<InvalidRunTransitionException>(() => RunStateMachine.EnsureAllowed(from, to));
    }

}

static void ReviewReadyIsUnavailableUntilEvidenceIntegration()
{
    foreach (var state in Enum.GetValues<RunState>())
        Assert.Throws<InvalidRunTransitionException>(() => RunStateMachine.EnsureAllowed(state, RunState.ReadyForHumanReview));
}

static void EventLogIsSequencedAndUtc()
{
    using var temp = new TemporaryDirectory();
    var path = Path.Combine(temp.Path, "events.jsonl");
    using (var store = JsonlRunEventStore.CreateNew(path, "run-1"))
    {
        store.Append("run_prepared", "accepted", to: RunState.Prepared,
            inputReference: "sha256:example", unknownInformation: ["commit未取得"]);
        store.Append("report_written", "accepted", to: RunState.Prepared,
            outputReference: "report.md", elapsedMilliseconds: 42);
    }

    var lines = File.ReadAllLines(path);
    Assert.Equal(2, lines.Length);
    using var first = JsonDocument.Parse(lines[0]);
    using var second = JsonDocument.Parse(lines[1]);
    Assert.Equal(1L, first.RootElement.GetProperty("sequence").GetInt64());
    Assert.Equal(2L, second.RootElement.GetProperty("sequence").GetInt64());
    var timestamp = DateTimeOffset.Parse(first.RootElement.GetProperty("timestampUtc").GetString()!);
    Assert.Equal(TimeSpan.Zero, timestamp.Offset, "時刻がUTCで記録されていません。");
    Assert.Equal("sha256:example", first.RootElement.GetProperty("inputReference").GetString());
    Assert.Equal("report.md", second.RootElement.GetProperty("outputReference").GetString());
    Assert.Equal(42L, second.RootElement.GetProperty("elapsedMilliseconds").GetInt64());
    Assert.Throws<IOException>(() => JsonlRunEventStore.CreateNew(path, "run-1"));
}

static void RejectedTransitionDoesNotMutate()
{
    using var temp = new TemporaryDirectory();
    var path = Path.Combine(temp.Path, "events.jsonl");
    using (var store = JsonlRunEventStore.CreateNew(path, "run-2"))
    {
        var run = new RunExecution(store);
        Assert.Throws<InvalidRunTransitionException>(() => run.Transition(RunState.ReadyForHumanReview));
        Assert.Equal(RunState.Prepared, run.State);
    }
    Assert.Equal(0, File.ReadAllLines(path).Length);
}

static void FakeSuccessKeepsReviewAndReadyEvidenceSeparate()
{
    using var temp = new TemporaryDirectory();
    var runDirectory = Path.Combine(temp.Path, "run");
    Directory.CreateDirectory(runDirectory);
    var eventPath = Path.Combine(runDirectory, "events.jsonl");
    FakeWorkflowResult result;
    using (var store = JsonlRunEventStore.CreateNew(eventPath, "fake-success"))
        result = new FakeAgentWorkflow().Run(SampleContract(), new string('a', 64), "fake-success", runDirectory,
            new FakeAgentRunner(FakeScenario.Success), store);

    Assert.Equal(RunState.NeedsHumanInput, result.State, "Fake review alone must not assert PR submission or acceptance verification.");
    var artifactPath = Path.Combine(runDirectory, result.ArtifactReference!);
    Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(artifactPath))), result.ArtifactSha256);
    Assert.True(File.Exists(Path.Combine(runDirectory, result.ReportReference)));
    var reviewerRequest = File.ReadAllText(Path.Combine(runDirectory, "exchanges", "reviewer-1", "request.json"));
    Assert.False(reviewerRequest.Contains("自己評価", StringComparison.Ordinal), "初回Reviewer入力にImplementerの自己評価が混入しています。");
    using var events = JsonDocument.Parse(File.ReadLines(eventPath).First(line => line.Contains("artifact_fixed", StringComparison.Ordinal)));
    Assert.Equal(result.ArtifactSha256, events.RootElement.GetProperty("diffSha256").GetString());
}

static void FakeRevisionIsBoundedAndReReviewed()
{
    using var temp = new TemporaryDirectory();
    var runDirectory = Path.Combine(temp.Path, "run");
    Directory.CreateDirectory(runDirectory);
    FakeWorkflowResult result;
    using (var store = JsonlRunEventStore.CreateNew(Path.Combine(runDirectory, "events.jsonl"), "fake-revision"))
        result = new FakeAgentWorkflow().Run(SampleContract(), new string('b', 64), "fake-revision", runDirectory,
            new FakeAgentRunner(FakeScenario.RevisionThenSuccess), store);

    Assert.Equal(RunState.NeedsHumanInput, result.State);
    Assert.Equal(1, result.RevisionAttempts);
    Assert.Equal(2, result.ImplementationCalls);
    Assert.Equal(2, result.ReviewCalls);
    Assert.True(File.ReadAllText(Path.Combine(runDirectory, "exchanges", "reviewer-2", "request.json"))
        .Contains("Reviewer", StringComparison.Ordinal));
    var eventLines = File.ReadAllLines(Path.Combine(runDirectory, "events.jsonl"));
    using var firstReviewEvent = JsonDocument.Parse(eventLines.First(line => line.Contains("\"role\":\"Reviewer\"", StringComparison.Ordinal)));
    Assert.Equal("exchanges/reviewer-1/request.json", firstReviewEvent.RootElement.GetProperty("inputReference").GetString());
    Assert.Equal(result.ArtifactSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(runDirectory, result.ArtifactReference!)))));
}

static void CodexOneShotStopsWithoutRetries()
{
    foreach (var scenario in new[] { FakeScenario.Success, FakeScenario.RevisionThenSuccess,
        FakeScenario.Stop, FakeScenario.ImplementerFailure, FakeScenario.ReviewerFailure,
        FakeScenario.EmptyResponse, FakeScenario.ReviewerEmptyResponse, FakeScenario.Timeout, FakeScenario.ReviewerTimeout })
    {
        using var temp = new TemporaryDirectory();
        var runDirectory = Path.Combine(temp.Path, "run");
        Directory.CreateDirectory(runDirectory);
        FakeWorkflowResult result;
        using (var store = JsonlRunEventStore.CreateNew(Path.Combine(runDirectory, "events.jsonl"), $"one-shot-{scenario}"))
            result = new FakeAgentWorkflow().Run(SampleContract(), new string('a', 64), $"one-shot-{scenario}", runDirectory,
                new FakeAgentRunner(scenario), store, codexOneShot: true);

        Assert.Equal(RunState.NeedsHumanInput, result.State, $"{scenario} が人間判断待ちで止まりませんでした。");
        Assert.True(result.ImplementationCalls <= 1, $"{scenario} でImplementerを複数回起動しました。");
        Assert.True(result.ReviewCalls <= 1, $"{scenario} でReviewerを複数回起動しました。");
        Assert.Equal(0, result.RevisionAttempts, $"{scenario} で自動修正しました。");
        Assert.Equal(0, result.RetryAttempts, $"{scenario} で再試行しました。");
        Assert.True(result.Detail is not null, $"{scenario} の停止理由が空です。");
        var events = File.ReadAllText(Path.Combine(runDirectory, "events.jsonl"));
        Assert.True(events.Contains("codex_one_shot_stopped", StringComparison.Ordinal), $"{scenario} の停止理由イベントがありません。");
        if (scenario == FakeScenario.ReviewerTimeout)
        {
            using var reviewerEvent = JsonDocument.Parse(File.ReadAllLines(Path.Combine(runDirectory, "events.jsonl"))
                .First(line => line.Contains("\"eventType\":\"agent_call\"", StringComparison.Ordinal) &&
                    line.Contains("\"role\":\"Reviewer\"", StringComparison.Ordinal)));
            Assert.Equal("timeout", reviewerEvent.RootElement.GetProperty("result").GetString());
            using var stopEvent = JsonDocument.Parse(File.ReadAllLines(Path.Combine(runDirectory, "events.jsonl"))
                .Last(line => line.Contains("codex_one_shot_stopped", StringComparison.Ordinal)));
            Assert.True(stopEvent.RootElement.GetProperty("detail").GetString()!.Contains("Reviewer結果=timeout", StringComparison.Ordinal));
            Assert.True(stopEvent.RootElement.GetProperty("detail").GetString()!.Contains("停止理由=Reviewerがタイムアウトしました。", StringComparison.Ordinal));
        }
        if (result.ArtifactSha256 is not null && scenario != FakeScenario.ReviewerEmptyResponse)
        {
            Assert.True(events.Contains(result.ArtifactSha256, StringComparison.Ordinal), $"{scenario} の成果物SHA-256が記録されていません。");
            Assert.True(File.Exists(Path.Combine(runDirectory, "exchanges", "reviewer-1", "response.md")),
                $"{scenario} のReviewer結果が記録されていません。");
        }
    }
}

static void FakeStopAndFailureScenariosAreRecorded()
{
    AssertScenario(FakeScenario.Stop, RunState.NeedsHumanInput, "要件に判断が必要");
    AssertScenario(FakeScenario.ImplementerFailure, RunState.Failed, "Implementerが失敗");
    AssertScenario(FakeScenario.EmptyResponse, RunState.Failed, "空応答");
    AssertScenario(FakeScenario.Timeout, RunState.NeedsHumanInput, "タイムアウト");
    AssertScenario(FakeScenario.ReviewerEmptyResponse, RunState.Failed, "空応答");
    AssertScenario(FakeScenario.ReviewerTimeout, RunState.NeedsHumanInput, "タイムアウト");

    static void AssertScenario(FakeScenario scenario, RunState expected, string detail)
    {
        using var temp = new TemporaryDirectory();
        var runDirectory = Path.Combine(temp.Path, "run");
        Directory.CreateDirectory(runDirectory);
        using var store = JsonlRunEventStore.CreateNew(Path.Combine(runDirectory, "events.jsonl"), $"fake-{scenario}");
        var result = new FakeAgentWorkflow().Run(SampleContract(), new string('c', 64), $"fake-{scenario}", runDirectory,
            new FakeAgentRunner(scenario), store);
        Assert.Equal(expected, result.State);
        Assert.True(result.Detail!.Contains(detail, StringComparison.Ordinal));
        Assert.True(File.ReadAllText(Path.Combine(runDirectory, "report.md")).Contains(result.State.ToString(), StringComparison.Ordinal));
    }
}

static void ReviewerRetriesStopAtLimit()
{
    using var temp = new TemporaryDirectory();
    var runDirectory = Path.Combine(temp.Path, "run");
    Directory.CreateDirectory(runDirectory);
    using var store = JsonlRunEventStore.CreateNew(Path.Combine(runDirectory, "events.jsonl"), "review-retries");
    var result = new FakeAgentWorkflow().Run(SampleContract(), new string('d', 64), "review-retries", runDirectory,
        new FakeAgentRunner(FakeScenario.ReviewerFailure), store);
    Assert.Equal(RunState.Failed, result.State);
    Assert.Equal(3, result.ReviewCalls);
    Assert.Equal(2, result.RetryAttempts);
}

static void RevisionLimitStopsForHuman()
{
    using var temp = new TemporaryDirectory();
    var runDirectory = Path.Combine(temp.Path, "run");
    Directory.CreateDirectory(runDirectory);
    using var store = JsonlRunEventStore.CreateNew(Path.Combine(runDirectory, "events.jsonl"), "revision-limit");
    var contract = SampleContract() with
    {
        Limits = SampleContract().Limits with { MaxRevisionAttempts = 0 }
    };
    var result = new FakeAgentWorkflow().Run(contract, new string('9', 64), "revision-limit", runDirectory,
        new FakeAgentRunner(FakeScenario.RevisionThenSuccess), store);
    Assert.Equal(RunState.NeedsHumanInput, result.State);
    Assert.Equal(0, result.RevisionAttempts);
    Assert.Equal(1, result.ImplementationCalls);
    Assert.True(result.Detail!.Contains("修正上限 0 回", StringComparison.Ordinal));
}

static void EventWriteFailureDoesNotClaimSuccess()
{
    using var temp = new TemporaryDirectory();
    var runDirectory = Path.Combine(temp.Path, "run");
    Directory.CreateDirectory(runDirectory);
    var writer = new FailingEventWriter("artifact_fixed");
    var result = new FakeAgentWorkflow().Run(SampleContract(), new string('e', 64), "write-fails", runDirectory,
        new FakeAgentRunner(FakeScenario.Success), writer);
    Assert.Equal(RunState.NeedsHumanInput, result.State);
    Assert.False(result.PersistenceSucceeded, "イベント保存失敗を永続化成功として返しました。");
    Assert.True(result.Detail!.Contains("保存失敗", StringComparison.Ordinal));
    Assert.True(File.Exists(Path.Combine(runDirectory, "artifacts", "artifact-1.md")), "保存済みの部分成果物が消去されています。");
    Assert.Equal(0, writer.ReadyTransitions);
}

static void ReportPersistenceFailuresAreConsistent()
{
    using (var temp = new TemporaryDirectory())
    {
        var runDirectory = Path.Combine(temp.Path, "stop-event-fails");
        Directory.CreateDirectory(runDirectory);
        var runner = new CountingFakeAgentRunner(FakeScenario.Success);
        FakeWorkflowResult result;
        using (var store = JsonlRunEventStore.CreateNew(Path.Combine(runDirectory, "events.jsonl"), "stop-event-fails"))
        {
            var writer = new FailingEventWriter("codex_one_shot_stopped", innerWriter: store);
            result = new FakeAgentWorkflow().Run(SampleContract(), new string('a', 64), "stop-event-fails", runDirectory,
                runner, writer, codexOneShot: true);
        }

        Assert.Equal(RunState.NeedsHumanInput, result.State);
        Assert.False(result.PersistenceSucceeded, "停止イベント保存失敗を永続化成功として返しました。");
        Assert.Equal(1, runner.ImplementationCalls, "停止イベント保存失敗後にImplementerを再起動しました。");
        Assert.Equal(1, runner.ReviewCalls, "停止イベント保存失敗後にReviewerを再起動しました。");
        var report = File.ReadAllText(Path.Combine(runDirectory, "report.md"));
        Assert.True(report.Contains("停止イベント codex_one_shot_stopped の保存に失敗", StringComparison.Ordinal),
            "停止イベント保存失敗が保存済みレポートにありません。");
        var events = File.ReadAllText(Path.Combine(runDirectory, "events.jsonl"));
        Assert.True(events.Contains("\"eventType\":\"report_written\"", StringComparison.Ordinal),
            "実在する報告を参照するreport_writtenイベントが記録されませんでした。");
        Assert.False(events.Contains("\"eventType\":\"codex_one_shot_stopped\"", StringComparison.Ordinal),
            "失敗注入した停止イベントがJSONLに記録されています。");
    }

    using (var temp = new TemporaryDirectory())
    {
        var runDirectory = Path.Combine(temp.Path, "report-write-fails");
        Directory.CreateDirectory(runDirectory);
        var runner = new CountingFakeAgentRunner(FakeScenario.Success);
        FakeWorkflowResult result;
        using (var store = JsonlRunEventStore.CreateNew(Path.Combine(runDirectory, "events.jsonl"), "report-write-fails"))
        {
            var writer = new FailingEventWriter("codex_one_shot_stopped", innerWriter: store);
            result = new FakeAgentWorkflow(new FailingFakeWorkflowStorage("report.md"))
                .Run(SampleContract(), new string('b', 64), "report-write-fails", runDirectory, runner, writer, codexOneShot: true);
        }

        Assert.Equal(RunState.NeedsHumanInput, result.State);
        Assert.False(result.PersistenceSucceeded, "レポートと停止イベントの保存失敗を成功として返しました。");
        Assert.True(result.Detail!.Contains("停止イベント codex_one_shot_stopped", StringComparison.Ordinal));
        Assert.True(result.Detail.Contains("報告 report.md", StringComparison.Ordinal));
        Assert.Equal(1, runner.ImplementationCalls, "永続化失敗後にImplementerを再起動しました。");
        Assert.Equal(1, runner.ReviewCalls, "永続化失敗後にReviewerを再起動しました。");
        Assert.False(File.Exists(Path.Combine(runDirectory, "report.md")), "保存失敗したレポートが存在しています。");
        var events = File.ReadAllText(Path.Combine(runDirectory, "events.jsonl"));
        Assert.True(events.Contains("\"eventType\":\"report_write_failed\"", StringComparison.Ordinal),
            "報告保存失敗イベントがJSONLに記録されませんでした。");
        Assert.False(events.Contains("\"eventType\":\"report_written\"", StringComparison.Ordinal),
            "保存失敗した報告にreport_writtenを記録しました。");
    }
}

static void ReportWrittenEventFailureUpdatesReport()
{
    using var temp = new TemporaryDirectory();
    var runDirectory = Path.Combine(temp.Path, "run");
    Directory.CreateDirectory(runDirectory);
    var runner = new CountingFakeAgentRunner(FakeScenario.Success);
    FakeWorkflowResult result;
    using (var store = JsonlRunEventStore.CreateNew(Path.Combine(runDirectory, "events.jsonl"), "report-event-fails"))
    {
        var writer = new FailingEventWriter("report_written", innerWriter: store);
        result = new FakeAgentWorkflow().Run(SampleContract(), new string('c', 64), "report-event-fails", runDirectory,
            runner, writer, codexOneShot: true);
    }

    Assert.Equal(RunState.NeedsHumanInput, result.State);
    Assert.False(result.PersistenceSucceeded, "report_written記録失敗を永続化成功として返しました。");
    Assert.Equal(1, runner.ImplementationCalls, "報告イベント記録失敗後にImplementerを再起動しました。");
    Assert.Equal(1, runner.ReviewCalls, "報告イベント記録失敗後にReviewerを再起動しました。");
    var report = File.ReadAllText(Path.Combine(runDirectory, "report.md"));
    Assert.True(report.Contains("report_written の記録に失敗", StringComparison.Ordinal),
        "report_written記録失敗後に報告が再保存されませんでした。");
    var events = File.ReadAllText(Path.Combine(runDirectory, "events.jsonl"));
    Assert.False(events.Contains("\"eventType\":\"report_written\"", StringComparison.Ordinal),
        "失敗注入したreport_writtenイベントがJSONLに記録されています。");
}

static void ReadyReportWrittenEventFailureUpdatesState()
{
    using var temp = new TemporaryDirectory();
    var runDirectory = Path.Combine(temp.Path, "run");
    Directory.CreateDirectory(runDirectory);
    var contract = SampleContract();
    var artifactSha256 = Convert.ToHexStringLower(SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes("成果物の初版を作成しました。")));
    var criteriaSha256 = Convert.ToHexStringLower(SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(string.Join("\n", contract.AcceptanceCriteria))));
    var reviewPath = Path.Combine(temp.Path, "review.json");
    var acceptancePath = Path.Combine(temp.Path, "acceptance.json");
    var pullRequestPath = Path.Combine(temp.Path, "pull-request.json");
    const string pullRequestUrl = "https://github.com/example/repository/pull/7";
    const string pullRequestOperationId = "github-op-7";
    File.WriteAllText(reviewPath, JsonSerializer.Serialize(new
    {
        evidenceKind = "independent-review",
        artifactSha256,
        reviewInputArtifactSha256 = artifactSha256,
        implementerId = "Implementer-1",
        reviewerId = "Reviewer-1",
        selfAssessmentExcluded = true,
        verdict = "approve"
    }));
    File.WriteAllText(acceptancePath, JsonSerializer.Serialize(new
    {
        evidenceKind = "acceptance-verification",
        criteriaSha256,
        criteriaCount = contract.AcceptanceCriteria.Count,
        passedCount = contract.AcceptanceCriteria.Count
    }));
    File.WriteAllText(pullRequestPath, JsonSerializer.Serialize(new
    {
        evidenceKind = "github-pull-request-created",
        operationId = pullRequestOperationId,
        url = pullRequestUrl
    }));
    var evidence = new HumanReviewEvidence(artifactSha256, "Implementer-1", "Reviewer-1", reviewPath,
        criteriaSha256, contract.AcceptanceCriteria.Count, contract.AcceptanceCriteria.Count, acceptancePath,
        pullRequestUrl, pullRequestOperationId, pullRequestPath);
    var runner = new CountingFakeAgentRunner(FakeScenario.Success);
    FakeWorkflowResult result;
    using (var store = JsonlRunEventStore.CreateNew(Path.Combine(runDirectory, "events.jsonl"), "ready-report-event-fails"))
    {
        var writer = new FailingEventWriter("report_written", innerWriter: store);
        result = new FakeAgentWorkflow().Run(contract, new string('d', 64), "ready-report-event-fails", runDirectory,
            runner, writer, evidence);
    }

    Assert.Equal(RunState.NeedsHumanInput, result.State);
    Assert.False(result.PersistenceSucceeded, "report_written記録失敗を永続化成功として返しました。");
    Assert.Equal(1, runner.ImplementationCalls);
    Assert.Equal(1, runner.ReviewCalls);
    var report = File.ReadAllText(Path.Combine(runDirectory, result.ReportReference));
    Assert.True(report.Contains("- 状態: `NeedsHumanInput`", StringComparison.Ordinal),
        "返却状態NeedsHumanInputが保存済み報告へ反映されていません。");
    Assert.False(report.Contains("- 状態: `ReadyForHumanReview`", StringComparison.Ordinal),
        "保存済み報告が古いReadyForHumanReview状態のままです。");
    Assert.True(report.Contains("report_written の記録に失敗", StringComparison.Ordinal),
        "報告保存イベント失敗が保存済み報告にありません。");
    var events = File.ReadAllText(Path.Combine(runDirectory, "events.jsonl"));
    Assert.False(events.Contains("\"eventType\":\"report_written\"", StringComparison.Ordinal),
        "失敗注入したreport_writtenイベントがJSONLに記録されています。");
}

static void FakeArtifactWriteFailureStopsAndReports()
{
    using var temp = new TemporaryDirectory();
    var runDirectory = Path.Combine(temp.Path, "run");
    Directory.CreateDirectory(runDirectory);
    var runner = new CountingFakeAgentRunner(FakeScenario.Success);
    FakeWorkflowResult result;
    using (var store = JsonlRunEventStore.CreateNew(Path.Combine(runDirectory, "events.jsonl"), "artifact-write-fails"))
        result = new FakeAgentWorkflow(new FailingFakeWorkflowStorage(Path.Combine("artifacts", "artifact-1.md")))
            .Run(SampleContract(), new string('f', 64), "artifact-write-fails", runDirectory, runner, store, codexOneShot: true);

    Assert.Equal(RunState.NeedsHumanInput, result.State);
    Assert.False(result.PersistenceSucceeded);
    Assert.Equal(1, runner.ImplementationCalls);
    Assert.Equal(0, runner.ReviewCalls, "成果物保存失敗後にReviewerを起動しました。");
    Assert.True(File.Exists(Path.Combine(runDirectory, "report.md")), "保存可能な場合の停止レポートがありません。");
    Assert.True(File.ReadAllText(Path.Combine(runDirectory, "report.md")).Contains("保存失敗", StringComparison.Ordinal));
    Assert.True(File.ReadAllText(Path.Combine(runDirectory, "events.jsonl")).Contains("codex_one_shot_stopped", StringComparison.Ordinal));
}

static void FakeMidwayWriteFailureStopsAndReports()
{
    using var temp = new TemporaryDirectory();
    var runDirectory = Path.Combine(temp.Path, "run");
    Directory.CreateDirectory(runDirectory);
    var runner = new CountingFakeAgentRunner(FakeScenario.Success);
    FakeWorkflowResult result;
    using (var store = JsonlRunEventStore.CreateNew(Path.Combine(runDirectory, "events.jsonl"), "review-write-fails"))
        result = new FakeAgentWorkflow(new FailingFakeWorkflowStorage(Path.Combine("exchanges", "reviewer-1", "response.md")))
            .Run(SampleContract(), new string('f', 64), "review-write-fails", runDirectory, runner, store, codexOneShot: true);

    Assert.Equal(RunState.NeedsHumanInput, result.State);
    Assert.False(result.PersistenceSucceeded);
    Assert.Equal(1, runner.ImplementationCalls);
    Assert.Equal(1, runner.ReviewCalls);
    Assert.True(File.Exists(Path.Combine(runDirectory, "report.md")), "保存可能な場合の停止レポートがありません。");
    Assert.True(File.ReadAllText(Path.Combine(runDirectory, "report.md")).Contains("保存失敗", StringComparison.Ordinal));
    Assert.True(File.ReadAllText(Path.Combine(runDirectory, "events.jsonl")).Contains("codex_one_shot_stopped", StringComparison.Ordinal));
}

static void ExistingRunDirectoryIsPreserved()
{
    using var temp = new TemporaryDirectory();
    var runDirectory = Path.Combine(temp.Path, "existing-run");
    Directory.CreateDirectory(runDirectory);
    var markerPath = Path.Combine(runDirectory, "existing.txt");
    File.WriteAllText(markerPath, "keep");
    using var writer = JsonlRunEventStore.CreateNew(Path.Combine(runDirectory, "events.jsonl"), "existing");
    Assert.Throws<IOException>(() => new FakeAgentWorkflow().Run(SampleContract(), new string('1', 64), "existing", runDirectory,
        new FakeAgentRunner(FakeScenario.Success), writer));
    Assert.Equal("keep", File.ReadAllText(markerPath));
    Assert.False(Directory.Exists(Path.Combine(runDirectory, "artifacts")));
}

static void ReadyRequiresIndependentEvidenceRecords()
{
    using var temp = new TemporaryDirectory();
    var artifactHash = new string('f', 64);
    var reviewPath = Path.Combine(temp.Path, "review.json");
    var acceptancePath = Path.Combine(temp.Path, "acceptance.json");
    var pullRequestPath = Path.Combine(temp.Path, "pull-request.json");
    var criteriaHash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", SampleContract().AcceptanceCriteria))));
    File.WriteAllText(reviewPath, JsonSerializer.Serialize(new { evidenceKind = "independent-review", artifactSha256 = artifactHash,
        reviewInputArtifactSha256 = artifactHash, implementerId = "Implementer-1", reviewerId = "Reviewer-1", selfAssessmentExcluded = true, verdict = "approve" }));
    File.WriteAllText(acceptancePath, JsonSerializer.Serialize(new { evidenceKind = "acceptance-verification", criteriaSha256 = criteriaHash, criteriaCount = 1, passedCount = 1 }));
    File.WriteAllText(pullRequestPath, JsonSerializer.Serialize(new { evidenceKind = "github-pull-request-created", operationId = "github-op-7", url = "https://github.com/example/repository/pull/7" }));
    using var store = JsonlRunEventStore.CreateNew(Path.Combine(temp.Path, "events.jsonl"), "evidence-gate");
    var run = new RunExecution(store, SampleContract());
    run.Transition(RunState.Implementing);
    run.Transition(RunState.Reviewing);
    Assert.Throws<InvalidRunTransitionException>(() => run.Transition(RunState.ReadyForHumanReview));
    Assert.Equal(RunState.Reviewing, run.State);
    Assert.Throws<InvalidRunTransitionException>(() => run.CompleteForHumanReview(new HumanReviewEvidence(
        artifactHash, "Implementer-1", "Reviewer-1", reviewPath, criteriaHash, 1, 0, acceptancePath,
        "https://github.com/example/repository/pull/7", "github-op-7", pullRequestPath)));
    Assert.Equal(RunState.Reviewing, run.State);
    Assert.Throws<InvalidRunTransitionException>(() => run.CompleteForHumanReview(new HumanReviewEvidence(
        artifactHash, "Implementer-1", "Reviewer-1", Path.Combine(temp.Path, "missing-review.json"), criteriaHash, 1, 1, acceptancePath,
        "https://github.com/example/repository/pull/7", "github-op-7", pullRequestPath)));
    Assert.Equal(RunState.Reviewing, run.State);

    run.CompleteForHumanReview(new HumanReviewEvidence(artifactHash, "Implementer-1", "Reviewer-1", reviewPath, criteriaHash, 1, 1,
        acceptancePath, "https://github.com/example/repository/pull/7", "github-op-7", pullRequestPath));
    Assert.Equal(RunState.ReadyForHumanReview, run.State);

    var failingWriter = new FailingEventWriter(string.Empty, failReadyTransition: true);
    var failedRun = new RunExecution(failingWriter, SampleContract());
    failedRun.Transition(RunState.Implementing);
    failedRun.Transition(RunState.Reviewing);
    Assert.Throws<IOException>(() => failedRun.CompleteForHumanReview(new HumanReviewEvidence(artifactHash, "Implementer-1", "Reviewer-1", reviewPath,
        criteriaHash, 1, 1, acceptancePath, "https://github.com/example/repository/pull/7", "github-op-7", pullRequestPath)));
    Assert.Equal(RunState.Reviewing, failedRun.State, "証跡記録失敗後に状態が進みました。");
}

static void EmptyRunPathsReturnInputError()
{
    using var temp = new TemporaryDirectory();
    var contractPath = Path.Combine(temp.Path, "contract.json");
    var contract = SampleContract() with { WorkingDirectory = temp.Path };
    File.WriteAllText(contractPath, JsonSerializer.Serialize(contract,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    AssertCliOptionRejected(contractPath, "--runs");
    AssertCliOptionRejected(contractPath, "--review-evidence");
    var emptyStartRuns = RunCli("start", contractPath, "--runs", "");
    Assert.Equal(2, emptyStartRuns.ExitCode, $"startの空--runsが終了コード2になりません: {emptyStartRuns.Error}");
    Assert.True(emptyStartRuns.Error.Contains("空のパス", StringComparison.Ordinal));

    static void AssertCliOptionRejected(string contractPath, string option)
    {
        var result = RunCli("run", contractPath, option, "");
        Assert.Equal(2, result.ExitCode, $"{option} の入力エラーが終了コード2ではありません: {result.Error}");
        Assert.True(result.Error.Contains("空のパス", StringComparison.Ordinal), $"{option} のエラーが利用者向けに表示されていません: {result.Error}");
    }
}

static void InvalidEvidenceReferenceIsRejected()
{
    using var temp = new TemporaryDirectory();
    var contract = SampleContract() with { WorkingDirectory = temp.Path };
    var contractPath = Path.Combine(temp.Path, "contract.json");
    var evidencePath = Path.Combine(temp.Path, "evidence.json");
    var runsPath = Path.Combine(temp.Path, "runs");
    File.WriteAllText(contractPath, JsonSerializer.Serialize(contract,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    var artifactHash = Convert.ToHexStringLower(SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes("成果物の初版を作成しました。" + Environment.NewLine)));
    var criteriaHash = Convert.ToHexStringLower(SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(string.Join("\n", contract.AcceptanceCriteria))));
    var acceptancePath = Path.Combine(temp.Path, "acceptance.json");
    var pullRequestPath = Path.Combine(temp.Path, "pull-request.json");
    File.WriteAllText(acceptancePath, JsonSerializer.Serialize(new
    {
        evidenceKind = "acceptance-verification", criteriaSha256 = criteriaHash, criteriaCount = 1, passedCount = 1
    }));
    const string pullRequestUrl = "https://github.com/example/repository/pull/7";
    File.WriteAllText(pullRequestPath, JsonSerializer.Serialize(new
    {
        evidenceKind = "github-pull-request-created", operationId = "github-op-7", url = pullRequestUrl
    }));
    File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
    {
        artifactSha256 = artifactHash,
        implementerId = "Implementer-1",
        reviewerId = "Reviewer-1",
        reviewEvidenceReference = "\0invalid-review-path",
        acceptanceCriteriaSha256 = criteriaHash,
        acceptanceCriteriaCount = 1,
        passedAcceptanceCriteriaCount = 1,
        acceptanceEvidenceReference = acceptancePath,
        pullRequestUrl,
        pullRequestOperationId = "github-op-7",
        pullRequestOperationRecordReference = pullRequestPath
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    var result = RunCli("run", contractPath, "--scenario", "Success", "--runs", runsPath,
        "--review-evidence", evidencePath);
    Assert.Equal(0, result.ExitCode, $"不正な証跡参照でCLIが異常終了しました: {result.Error}");
    Assert.True(result.Output.Contains("状態: NeedsHumanInput", StringComparison.Ordinal),
        $"不正な証跡参照が拒否されずに進行しました: {result.Output}");
    var runDirectory = Directory.GetDirectories(runsPath).Single();
    Assert.True(File.ReadAllText(Path.Combine(runDirectory, "report.md")).Contains("記録を照合できません", StringComparison.Ordinal));
}

static void ReviewEvidenceReadErrorsAreInputFailures()
{
    using var temp = new TemporaryDirectory();
    var contract = SampleContract() with { WorkingDirectory = temp.Path };
    var contractPath = Path.Combine(temp.Path, "contract.json");
    File.WriteAllText(contractPath, JsonSerializer.Serialize(contract,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    var missingEvidencePath = Path.Combine(temp.Path, "missing-evidence.json");
    var missingRunsPath = Path.Combine(temp.Path, "runs-missing-evidence");
    var missing = RunCli("run", contractPath, "--runs", missingRunsPath, "--review-evidence", missingEvidencePath);
    Assert.Equal(2, missing.ExitCode, $"存在しない証跡ファイルが入力エラーになりません: {missing.Error}");
    Assert.True(missing.Error.Contains("証跡ファイルを読み取れません", StringComparison.Ordinal));
    Assert.False(Directory.Exists(missingRunsPath), "証跡ファイルの入力エラー後に実行先が作成されました。");

    var malformedEvidencePath = Path.Combine(temp.Path, "malformed-evidence.json");
    var malformedRunsPath = Path.Combine(temp.Path, "runs-malformed-evidence");
    File.WriteAllText(malformedEvidencePath, "{");
    var malformed = RunCli("run", contractPath, "--runs", malformedRunsPath, "--review-evidence", malformedEvidencePath);
    Assert.Equal(2, malformed.ExitCode, $"不正な証跡JSONが入力エラーになりません: {malformed.Error}");
    Assert.True(malformed.Error.Contains("証跡JSONが不正です", StringComparison.Ordinal));
    Assert.False(Directory.Exists(malformedRunsPath), "証跡JSONの入力エラー後に実行先が作成されました。");
}

static void TestSuiteOutcomeContractIsConsistent()
{
    var passOnly = new TestSuite();
    passOnly.Add("pass-a", static () => { });
    passOnly.Add("pass-b", static () => { });
    var passResult = passOnly.Run(TextWriter.Null, TextWriter.Null);
    Assert.Equal(new TestSuiteResult(2, 0, 0, 2, 0), passResult,
        "Passのみの件数または終了コードが契約と一致しません。");

    using var passOutput = new StringWriter();
    var passLog = new TestSuite();
    passLog.Add("pass", static () => { });
    var loggedPassResult = passLog.Run(passOutput, TextWriter.Null);
    Assert.Equal(new TestSuiteResult(1, 0, 0, 1, 0), loggedPassResult);
    Assert.True(passOutput.ToString().Contains("結果: 1成功, 0未検証, 0失敗 / 1項目", StringComparison.Ordinal),
        "Passのみの要約ログが件数と一致しません。");

    using var skipOutput = new StringWriter();
    using var skipError = new StringWriter();
    var skipOnly = new TestSuite();
    skipOnly.Add("skip-a", () => Assert.Skip("fixture skip"));
    skipOnly.Add("skip-b", () => Assert.Skip("fixture skip 2"));
    var skipResult = skipOnly.Run(skipOutput, skipError);
    Assert.Equal(new TestSuiteResult(0, 2, 0, 2, 2), skipResult,
        "Skipのみの件数または終了コードが契約と一致しません。");
    Assert.True(skipOutput.ToString().Contains("未検証: skip-a: fixture skip", StringComparison.Ordinal),
        "Skip理由が未検証ログに残りません。");
    Assert.True(skipOutput.ToString().Contains("結果: 0成功, 2未検証, 0失敗 / 2項目", StringComparison.Ordinal),
        "Skipのみの要約ログが件数と一致しません。");
    Assert.Equal(string.Empty, skipError.ToString(), "Skipが失敗ログへ混ざりました。");

    using var failOutput = new StringWriter();
    using var failError = new StringWriter();
    var failOnly = new TestSuite();
    failOnly.Add("fail-a", static () => throw new InvalidOperationException("fixture failure"));
    var failResult = failOnly.Run(failOutput, failError);
    Assert.Equal(new TestSuiteResult(0, 0, 1, 1, 1), failResult,
        "Failのみの件数または終了コードが契約と一致しません。");
    Assert.True(failError.ToString().Contains("失敗: fail-a: fixture failure", StringComparison.Ordinal),
        "Failのみの理由がstderrへ残りません。");
    Assert.True(failOutput.ToString().Contains("結果: 0成功, 0未検証, 1失敗 / 1項目", StringComparison.Ordinal),
        "Failのみの要約ログが件数と一致しません。");

    using var mixedOutput = new StringWriter();
    using var mixedError = new StringWriter();
    var mixed = new TestSuite();
    mixed.Add("pass", static () => { });
    mixed.Add("skip", () => Assert.Skip("mixed fixture skip"));
    mixed.Add("fail", static () => throw new InvalidOperationException("mixed fixture failure"));
    var mixedResult = mixed.Run(mixedOutput, mixedError);
    Assert.Equal(new TestSuiteResult(1, 1, 1, 3, 1), mixedResult,
        "FailとSkip混在時にFail優先または件数の契約が守られません。");
    Assert.True(mixedOutput.ToString().Contains("未検証: skip: mixed fixture skip", StringComparison.Ordinal),
        "FailとSkip混在時にSkipログが失われました。");
    Assert.True(mixedOutput.ToString().Contains("結果: 1成功, 1未検証, 1失敗 / 3項目", StringComparison.Ordinal),
        "FailとSkip混在時の要約ログが件数と一致しません。");
    Assert.True(mixedError.ToString().Contains("失敗: fail: mixed fixture failure", StringComparison.Ordinal),
        "Fail理由がstderrへ残りません。");
    Assert.Equal(0, passResult.Passed + passResult.Skipped + passResult.Failed - passResult.Total,
        "Passのみの要約件数がテスト総数と一致しません。");
    Assert.Equal(0, skipResult.Passed + skipResult.Skipped + skipResult.Failed - skipResult.Total,
        "Skipのみの要約件数がテスト総数と一致しません。");
    Assert.Equal(0, failResult.Passed + failResult.Skipped + failResult.Failed - failResult.Total,
        "Failのみの要約件数がテスト総数と一致しません。");
    Assert.Equal(0, mixedResult.Passed + mixedResult.Skipped + mixedResult.Failed - mixedResult.Total,
        "Fail/Skip混在の要約件数がテスト総数と一致しません。");
}

static (int ExitCode, string Output, string Error) RunCli(params string[] arguments)
{
    DirectoryInfo? repoRoot = new(AppContext.BaseDirectory);
    while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "Ateliers.Ai.Orchestrator.sln")))
        repoRoot = repoRoot.Parent;
    Assert.True(repoRoot is not null, "テスト実行時にリポジトリルートを特定できません。");

    var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
    var cliProject = Path.Combine(repoRoot!.FullName, "src", "Ateliers.Ai.Orchestrator.Cli", "Ateliers.Ai.Orchestrator.Cli.csproj");
    var start = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = repoRoot.FullName,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    foreach (var argument in new[] { "run", "--project", cliProject, "--configuration", configuration, "--no-build", "--" }.Concat(arguments))
        start.ArgumentList.Add(argument);

    using var process = Process.Start(start) ?? throw new Exception("CLIプロセスを開始できません。");
    if (!process.WaitForExit(30_000))
    {
        process.Kill(entireProcessTree: true);
        throw new Exception("CLIが時間内に終了しませんでした。");
    }
    var standardOutput = process.StandardOutput.ReadToEnd();
    var standardError = process.StandardError.ReadToEnd();
    return (process.ExitCode, standardOutput, standardError);
}

static TaskContract SampleContract() => new()
{
    SchemaVersion = 1,
    TaskId = "ORCH-3",
    Purpose = "契約検証",
    WorkingDirectory = Path.GetFullPath(Path.GetTempPath()),
    Scope = new TaskScope { Included = ["src"], Excluded = ["bin"] },
    Inputs = [new TaskInput { Description = "要件", Reference = "issue://3", Revision = "current" }],
    Requirements = ["契約を検証する"],
    AcceptanceCriteria = ["不正契約を拒否する"],
    DelegatedOperations = [],
    Limits = new ExecutionLimits { MaxDurationMinutes = 60, MaxRevisionAttempts = 1, MaxRetries = 2 },
    StopConditions = ["上限到達"],
    Reporting = new ReportingRequirements { PullRequestExpected = true, FinalReportFormat = "markdown" }
};

/// <summary>依存パッケージを使わずに検証項目を実行するテスト集です。</summary>
sealed class TestSuite
{
    private readonly List<(string Name, Action Test)> tests = [];
    /// <summary>名前付きの検証処理をテスト集へ追加します。</summary>
    /// <param name="name">結果表示に使う検証名です。</param>
    /// <param name="test">実行する検証処理です。</param>
    public void Add(string name, Action test) => tests.Add((name, test));
    /// <summary>登録した検証を順に実行し、件数・ログ・終了コードを同じ分類結果から生成します。</summary>
    /// <returns>全件成功は0、失敗ありは1、失敗なしで未検証ありは2の結果です。</returns>
    public TestSuiteResult Run(TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        var passed = 0;
        var skipped = 0;
        var failed = 0;
        foreach (var (name, test) in tests)
        {
            try { test(); passed++; output.WriteLine($"成功: {name}"); }
            catch (TestSkippedException exception)
            {
                skipped++;
                output.WriteLine($"未検証: {name}: {exception.Message}");
            }
            catch (DiagnosticTestFailure exception) { failed++; error.WriteLine($"失敗: {name}: {exception.Message}"); }
            catch (Exception exception) { failed++; error.WriteLine($"失敗: {name}: {exception.Message}"); }
        }
        var exitCode = failed > 0 ? 1 : skipped > 0 ? 2 : 0;
        var result = new TestSuiteResult(passed, skipped, failed, tests.Count, exitCode);
        output.WriteLine($"結果: {result.Passed}成功, {result.Skipped}未検証, {result.Failed}失敗 / {result.Total}項目");
        return result;
    }
}

/// <summary>テスト集の表示件数とプロセス終了コードに使う共通の最終集計です。</summary>
/// <param name="Passed">正常に完了した検証数です。</param>
/// <param name="Skipped">環境制約などで未検証となった検証数です。</param>
/// <param name="Failed">例外により失敗した検証数です。</param>
/// <param name="Total">実行した検証数です。</param>
/// <param name="ExitCode">Passのみは0、Failを含めば1、FailなしでSkipを含めば2です。</param>
sealed record TestSuiteResult(int Passed, int Skipped, int Failed, int Total, int ExitCode);

sealed record ExternalProcessResult(int? ExitCode, bool TimedOut, bool ProcessExitObserved, bool KillAttempted,
    bool KillRequestSucceeded, int StandardOutputCharacters, int StandardErrorCharacters, bool ExecutableNotFound,
    string? StartFailureType, int? NativeErrorCode)
{
    public static ExternalProcessResult StartFailure(string failureType, int? nativeErrorCode, bool executableNotFound) =>
        new(null, false, false, false, false, -1, -1, executableNotFound, failureType, nativeErrorCode);

    public static ExternalProcessResult Completed(int? exitCode, bool timedOut, bool processExitObserved,
        bool killAttempted, bool killSucceeded, int standardOutputCharacters, int standardErrorCharacters) =>
        new(exitCode, timedOut, processExitObserved, killAttempted, killSucceeded, standardOutputCharacters,
            standardErrorCharacters, false, null, null);
}

sealed class TestSkippedException(string message) : Exception(message);

/// <summary>テスト用の最小限の条件確認と例外確認を提供します。</summary>
static class Assert
{
    /// <summary>環境制約により実施できない検証を成功件数から分けます。</summary>
    /// <param name="reason">検証できなかった理由です。</param>
    public static void Skip(string reason) => throw new TestSkippedException(reason);
    /// <summary>条件が真であることを確認します。</summary>
    public static void True(bool condition, string message = "条件が真ではありません。") { if (!condition) throw new Exception(message); }
    /// <summary>条件が偽であることを確認します。</summary>
    public static void False(bool condition, string message = "条件が偽ではありません。") => True(!condition, message);
    /// <summary>期待値と実際の値が等しいことを確認します。</summary>
    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(message ?? $"期待値: {expected} / 実際: {actual}");
    }
    /// <summary>処理が指定した例外型を投げることを確認します。</summary>
    /// <typeparam name="TException">期待する例外型です。</typeparam>
    /// <param name="action">例外の有無を確認する処理です。</param>
    /// <returns>処理が投げた例外です。</returns>
    public static TException Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException exception) { return exception; }
        throw new Exception($"{typeof(TException).Name} が発生しませんでした。");
    }
}

/// <summary>テストごとに一意な一時ディレクトリを作成し、終了時に削除します。</summary>
sealed class TemporaryDirectory : IDisposable
{
    /// <summary>このテスト用に作成された一時ディレクトリの絶対パスです。</summary>
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"orchestrator-tests-{Guid.NewGuid():N}");
    /// <summary>Git fixtureなどOSプロセスが作成した追加作業領域です。</summary>
    public string? AdditionalPath { get; set; }
    /// <summary>一時ディレクトリを作成します。</summary>
    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    /// <summary>一時ディレクトリとその内容を削除します。</summary>
    public void Dispose()
    {
        if (AdditionalPath is not null && Directory.Exists(AdditionalPath))
        {
            foreach (var file in Directory.EnumerateFiles(AdditionalPath, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            foreach (var directory in Directory.EnumerateDirectories(AdditionalPath, "*", SearchOption.AllDirectories).Reverse())
                File.SetAttributes(directory, FileAttributes.Directory);
            File.SetAttributes(AdditionalPath, FileAttributes.Directory);
            Directory.Delete(AdditionalPath, recursive: true);
        }
        Directory.Delete(Path, recursive: true);
    }
}

sealed class FailingFakeWorkflowStorage(string failingPathFragment) : IFakeWorkflowStorage
{
    public void WriteAllText(string path, string content, System.Text.Encoding encoding)
    {
        if (path.Contains(failingPathFragment, StringComparison.OrdinalIgnoreCase))
            throw new IOException("テスト用のファイル保存失敗です。");
        File.WriteAllText(path, content, encoding);
    }

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
}

sealed class CountingFakeAgentRunner(FakeScenario scenario) : IAgentRunner
{
    private readonly FakeAgentRunner inner = new(scenario);
    public int ImplementationCalls { get; private set; }
    public int ReviewCalls { get; private set; }

    public AgentResponse Implement(AgentRequest request)
    {
        ImplementationCalls++;
        return inner.Implement(request);
    }

    public AgentResponse Review(AgentRequest request)
    {
        ReviewCalls++;
        return inner.Review(request);
    }
}

sealed class FailingEventWriter(string failureEventType, bool failReadyTransition = false, IRunEventWriter? innerWriter = null) : IRunEventWriter
{
    private long sequence;
    public int ReadyTransitions { get; private set; }

    public RunEvent Append(string eventType, string result, RunState? from = null, RunState? to = null,
        string? detail = null, string? error = null, string? inputReference = null,
        string? outputReference = null, string? targetCommitSha = null, string? diffSha256 = null,
        long? elapsedMilliseconds = null, IReadOnlyList<string>? unknownInformation = null,
        IReadOnlyList<string>? artifactReferences = null,
        string role = "Orchestrator")
    {
        if (to == RunState.ReadyForHumanReview) ReadyTransitions++;
        if (eventType == failureEventType || (failReadyTransition && to == RunState.ReadyForHumanReview))
            throw new IOException("テスト用の記録失敗です。");
        if (innerWriter is not null)
            return innerWriter.Append(eventType, result, from, to, detail, error, inputReference, outputReference,
                targetCommitSha, diffSha256, elapsedMilliseconds, unknownInformation, artifactReferences, role);
        return new RunEvent
        {
            RunId = "test",
            Sequence = ++sequence,
            TimestampUtc = DateTimeOffset.UtcNow,
            Role = role,
            EventType = eventType,
            Result = result,
            FromState = from,
            ToState = to,
            Detail = detail,
            Error = error,
            ArtifactReferences = artifactReferences ?? [],
            InputReference = inputReference,
            OutputReference = outputReference,
            TargetCommitSha = targetCommitSha,
            DiffSha256 = diffSha256,
            ElapsedMilliseconds = elapsedMilliseconds,
            UnknownInformation = unknownInformation ?? []
        };
    }
}

sealed class DiagnosticTestFailure(string details) : Exception(details)
{
}

