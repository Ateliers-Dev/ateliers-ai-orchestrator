using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using Ateliers.Ai.Orchestrator.Core;

/// <summary>Codex CLIのJSONL実行をCoreのAgentRunner境界へ接続します。</summary>
internal sealed class CodexCliAgentRunner : IAgentRunner
{
    private const string ScopeBoundaryInstruction = "作業範囲はAgentへの指示であり、OSレベルで範囲外アクセスを完全に防ぐ保証ではありません。";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly CodexCliRunnerOptions options;
    private readonly ConcurrentDictionary<string, string> initialCommits = new(StringComparer.Ordinal);

    /// <summary>CLI実行ファイルと任意の役割別プロファイルを設定します。</summary>
    /// <param name="options">Codex CLIの実行設定です。</param>
    public CodexCliAgentRunner(CodexCliRunnerOptions options) => this.options = options.Validate();

    /// <inheritdoc />
    public AgentResponse Implement(AgentRequest request) => Invoke(request, "workspace-write");

    /// <inheritdoc />
    public AgentResponse Review(AgentRequest request) => Invoke(request, "workspace-write");

    private AgentResponse Invoke(AgentRequest request, string sandbox) => InvokeAsync(request, sandbox).GetAwaiter().GetResult();

    private async Task<AgentResponse> InvokeAsync(AgentRequest request, string sandbox)
    {
        var role = request.Role == "Reviewer" ? "reviewer" : "implementer";
        var profileRoot = CodexCliRunnerOptions.ResolveCodexHome(
            role == "reviewer" ? options.ReviewerCodexHome : options.ImplementerCodexHome);
        var runDirectory = Path.GetFullPath(request.RunDirectory ?? throw new InvalidOperationException("実行記録先が必要です。"));
        request = request with { RunDirectory = runDirectory };
        var prompt = BuildPrompt(request);
        var evidenceDirectory = Path.Combine(runDirectory,
            "exchanges", $"{role}-{request.Attempt}");
        Directory.CreateDirectory(evidenceDirectory);
        var reviewerWorkspace = role == "reviewer" ? Path.Combine(evidenceDirectory, "workspace") : null;
        var artifactDirectory = reviewerWorkspace is null ? null : Path.Combine(reviewerWorkspace, "artifacts");
        if (reviewerWorkspace is not null)
        {
            Directory.CreateDirectory(reviewerWorkspace);
            Directory.CreateDirectory(artifactDirectory!);
            // Codex CLI requires its working directory to be inside a Git repository.
            // Keep that repository inside the isolated run evidence, not the implementer's checkout.
            RunGit(reviewerWorkspace, "init", "--quiet");
        }
        var stdoutPath = Path.Combine(evidenceDirectory, "codex.stdout.jsonl");
        var stderrPath = Path.Combine(evidenceDirectory, "codex.stderr.txt");
        // The approved PoC must not launch an extra `codex --version` process.
        // Version metadata is intentionally recorded as unavailable.
        const string? cliVersion = null;
        string? error = null;
        var timedOut = false;
        var stopRequested = false;
        string? stopReason = null;
        var start = CreateProcessStartInfo(options.ExecutablePath, role, request, artifactDirectory);
        start.UseShellExecute = false;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        start.StandardInputEncoding = StrictUtf8;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        var codexArguments = new List<string>(options.PrefixArguments ?? []);
        codexArguments.AddRange(["exec", "--json", "--sandbox", sandbox, "--config", "approval_policy=\"never\""]);
        if (role == "reviewer")
        {
            codexArguments.Add("--config");
            codexArguments.Add("sandbox_workspace_write.writable_roots=[]");
        }
        codexArguments.Add("-");
        if (OperatingSystem.IsWindows() && string.Equals(Path.GetExtension(options.ExecutablePath), ".ps1", StringComparison.OrdinalIgnoreCase))
            AddPowerShellShimCommand(start, options.ExecutablePath, codexArguments);
        else
            foreach (var argument in codexArguments) start.ArgumentList.Add(argument);
        PrepareEnvironment(start, profileRoot);
        var redactOutput = CreateProxyOutputRedactor(start.Environment);
        if (role == "implementer" && request.Attempt == 1)
        {
            try { initialCommits[request.RunId] = PrepareCleanGitWorkspace(request.Contract.WorkingDirectory); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            { error = exception.Message; }
        }
        Process? process = null;
        var killAttempted = false;
        bool? processExitObserved = null;
        bool? streamsClosed = null;
        int? exitCode = null;
        int? processId = null;
        string? threadId = null;
        string? terminalEvent = null;
        string? usageJson = null;
        var response = new StringBuilder();

        try
        {
            if (error is not null) throw new InvalidOperationException(error);
            process = Process.Start(start) ?? throw new InvalidOperationException("Codex CLIプロセスを開始できませんでした。");
            processId = process.Id;
            using var stdoutWriter = new StreamWriter(new FileStream(stdoutPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            using var stderrWriter = new StreamWriter(new FileStream(stderrPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            var stdoutTask = PumpStdoutAsync(process, stdoutWriter, redactOutput, text =>
            {
                if (!TryParseEvent(text, out var type, out var eventThreadId, out var message, out var usage, out var parseError))
                {
                    error ??= parseError;
                    return;
                }
                if (!string.IsNullOrWhiteSpace(eventThreadId)) threadId = eventThreadId;
                if (!string.IsNullOrWhiteSpace(message)) response.Append(message);
                if (type is "turn.completed" or "turn.failed" or "error") terminalEvent = type;
                if (usage is not null) usageJson = usage;
            });
            var stderrTask = PumpStderrAsync(process, stderrWriter, redactOutput);
            // Write bytes directly so neither the console code page nor a StreamWriter preamble can alter the CLI prompt.
            var promptBytes = StrictUtf8.GetBytes(prompt);
            await process.StandardInput.BaseStream.WriteAsync(promptBytes);
            await process.StandardInput.BaseStream.FlushAsync();
            process.StandardInput.Close();
            var timeout = request.RemainingTime is { } remaining && remaining < TimeSpan.FromMinutes(request.Contract.Limits.MaxDurationMinutes)
                ? remaining : TimeSpan.FromMinutes(request.Contract.Limits.MaxDurationMinutes);
            var exitTask = process.WaitForExitAsync();
            var cancellationToken = request.StopToken.CanBeCanceled ? request.StopToken : options.CancellationToken;
            var stopTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var completedTask = await Task.WhenAny(exitTask, Task.Delay(timeout), stopTask);
            if (completedTask != exitTask)
            {
                stopRequested = true;
                timedOut = completedTask != stopTask;
                stopReason = timedOut ? "timeout" : "caller-cancellation";
                killAttempted = true;
                try { (options.KillProcessForTesting ?? (child => child.Kill(entireProcessTree: true)))(process); }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                { error = $"プロセス終了要求に失敗しました: {exception.Message}"; }
            }
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                processExitObserved = true;
                exitCode = process.ExitCode;
            }
            catch (TimeoutException)
            {
                processExitObserved = false;
                error ??= "停止要求後もプロセス終了を10秒以内に確認できませんでした。";
            }
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(10));
                streamsClosed = true;
            }
            catch (TimeoutException)
            {
                streamsClosed = false;
                error ??= "停止要求後も標準出力または標準エラーの切断を10秒以内に確認できませんでした。";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            error = redactOutput(exception.Message);
            if (!File.Exists(stderrPath)) File.WriteAllText(stderrPath, error + Environment.NewLine, new UTF8Encoding(false));
            if (process is { HasExited: false })
            {
                killAttempted = true;
                try { (options.KillProcessForTesting ?? (child => child.Kill(entireProcessTree: true)))(process); } catch (Exception killError) { error += $"; 停止要求失敗: {redactOutput(killError.Message)}"; }
                try { process.WaitForExit(10_000); processExitObserved = process.HasExited; if (process.HasExited) exitCode = process.ExitCode; }
                catch (InvalidOperationException) { processExitObserved = false; }
            }
        }
        finally { process?.Dispose(); }

        IReadOnlyList<string> artifactReferences = [];
        if (artifactDirectory is not null)
        {
            try { artifactReferences = CollectArtifactReferences(artifactDirectory, runDirectory); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error ??= $"Reviewer任意成果物の参照を取得できませんでした: {exception.Message}";
            }
        }
        var hasResponse = !string.IsNullOrWhiteSpace(response.ToString());
        var hasArtifacts = artifactReferences.Count > 0;
        var succeeded = !stopRequested && !timedOut && error is null && terminalEvent == "turn.completed" && exitCode == 0 &&
            (role == "reviewer" ? hasResponse || hasArtifacts : hasResponse);
        if (!succeeded && error is null)
            error = stopReason == "caller-cancellation" ? "利用者の停止要求を受け付けました。" : timedOut ? "実行時間上限に到達しました。" :
                exitCode is { } failedExitCode && failedExitCode != 0
                ? BuildCliFailureDiagnostic(failedExitCode, terminalEvent,
                    Path.GetRelativePath(request.RunDirectory!, stderrPath).Replace('\\', '/'))
                : role == "reviewer" && terminalEvent == "turn.completed" && exitCode == 0 && !hasResponse && !hasArtifacts
                    ? "Reviewerは本文も任意成果物も返しませんでした。" :
                $"完了条件不一致: terminal={terminalEvent ?? "未取得"}, exit={exitCode?.ToString() ?? "未取得"}, response={(string.IsNullOrWhiteSpace(response.ToString()) ? "空" : "取得済み")}";
        var content = response.ToString();
        string? selfAssessment = null;
        if (succeeded && role == "implementer")
        {
            try
            {
                var baseCommit = initialCommits[request.RunId];
                var changes = CaptureChanges(request.Contract.WorkingDirectory, baseCommit);
                selfAssessment = content;
                content = changes;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                succeeded = false;
                error = $"作業領域の変更差分を固定できませんでした: {exception.Message}";
            }
        }
        error = error is null ? null : redactOutput(error);
        var evidence = new AgentExecutionEvidence(processId, threadId, cliVersion, terminalEvent, exitCode, usageJson,
            Path.GetRelativePath(request.RunDirectory!, stdoutPath).Replace('\\', '/'),
            Path.GetRelativePath(request.RunDirectory!, stderrPath).Replace('\\', '/'), timedOut, killAttempted,
            processExitObserved, streamsClosed, stopRequested, stopReason, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))),
            start.WorkingDirectory, request.ArtifactSha256, error);
        return new AgentResponse(content, succeeded, timedOut, SelfAssessment: selfAssessment,
            ExecutionEvidence: evidence, StopRequested: stopRequested, ArtifactReferences: artifactReferences);
    }

    private static ProcessStartInfo CreateProcessStartInfo(string executablePath, string role, AgentRequest request, string? artifactDirectory)
    {
        var start = new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = role == "implementer" ? request.Contract.WorkingDirectory : Path.GetDirectoryName(artifactDirectory)!
        };

        // npm installs a PowerShell shim alongside codex.cmd. A .ps1 file is not a native executable,
        // so invoke it through Windows PowerShell with the CLI arguments quoted as PowerShell literals.
        if (OperatingSystem.IsWindows() && string.Equals(Path.GetExtension(executablePath), ".ps1", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = "powershell.exe";
            start.ArgumentList.Add("-NoLogo");
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-Command");
        }
        return start;
    }

    private static void AddPowerShellShimCommand(ProcessStartInfo start, string executablePath, IReadOnlyList<string> arguments)
    {
        static string QuotePowerShellLiteral(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
        var argumentList = string.Join(", ", arguments.Select(QuotePowerShellLiteral));
        var command = $"$scriptPath = {QuotePowerShellLiteral(Path.GetFullPath(executablePath))}; " +
            $"$codexArgs = @({argumentList}); & $scriptPath @codexArgs; exit $LASTEXITCODE";
        start.ArgumentList.Add(command);
    }

    private static string BuildCliFailureDiagnostic(int exitCode, string? terminalEvent, string stderrReference) =>
        $"Codex CLIが終了コード {exitCode} で終了しました。terminal={terminalEvent ?? "未取得"}; stderrログ参照: {stderrReference}";

    private static Func<string, string> CreateProxyOutputRedactor(IDictionary<string, string?> environment)
    {
        var proxyVariables = new HashSet<string>(
            ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy"],
            StringComparer.OrdinalIgnoreCase);
        var secrets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, value) in environment)
        {
            if (!proxyVariables.Contains(name) || string.IsNullOrWhiteSpace(value)) continue;
            secrets.Add(value);
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo)) continue;

            secrets.Add(uri.AbsoluteUri);
            var userInfo = uri.UserInfo;
            AddCredentialParts(userInfo);
            AddCredentialParts(Uri.UnescapeDataString(userInfo));
        }

        var orderedSecrets = secrets.Where(secret => secret.Length > 0)
            .OrderByDescending(secret => secret.Length).ToArray();
        return output =>
        {
            foreach (var secret in orderedSecrets)
                output = output.Replace(secret, "[REDACTED_PROXY]", StringComparison.Ordinal);
            return output;
        };

        void AddCredentialParts(string userInfo)
        {
            secrets.Add(userInfo);
            var separator = userInfo.IndexOf(':');
            if (separator < 0) return;
            secrets.Add(userInfo[..separator]);
            secrets.Add(userInfo[(separator + 1)..]);
        }
    }

    private static async Task PumpStdoutAsync(Process process, StreamWriter writer, Func<string, string> redact, Action<string> onLine)
    {
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            line = redact(line);
            await writer.WriteLineAsync(line);
            await writer.FlushAsync();
            onLine(line);
        }
    }

    private static async Task PumpStderrAsync(Process process, StreamWriter writer, Func<string, string> redact)
    {
        while (await process.StandardError.ReadLineAsync() is { } line)
        {
            await writer.WriteLineAsync(redact(line));
            await writer.FlushAsync();
        }
    }

    private static bool TryParseEvent(string line, out string? type, out string? threadId,
        out string? message, out string? usage, out string? error)
    {
        type = threadId = message = usage = error = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            threadId = root.TryGetProperty("thread_id", out var threadElement) ? threadElement.GetString() : null;
            if (type == "item.completed" && root.TryGetProperty("item", out var item) &&
                item.TryGetProperty("type", out var itemType) && itemType.GetString() == "agent_message" &&
                item.TryGetProperty("text", out var text)) message = text.GetString();
            if (root.TryGetProperty("usage", out var usageElement)) usage = usageElement.GetRawText();
            if (type == "turn.completed" && root.TryGetProperty("turn", out var turn) &&
                turn.TryGetProperty("usage", out var turnUsage)) usage = turnUsage.GetRawText();
            return true;
        }
        catch (JsonException exception)
        {
            error = $"JSONLイベントの解析に失敗しました: {exception.Message}";
            return false;
        }
    }

    private static string BuildPrompt(AgentRequest request)
    {
        var contract = JsonSerializer.Serialize(request.Contract, new JsonSerializerOptions { WriteIndented = true });
        if (request.Role == "Reviewer")
        {
            var artifactPath = ResolveArtifactPath(request);
            var artifactBytes = File.ReadAllBytes(artifactPath);
            var actualArtifactSha256 = Convert.ToHexStringLower(SHA256.HashData(artifactBytes));
            if (!string.Equals(actualArtifactSha256, request.ArtifactSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Reviewerへ渡す固定成果物のSHA-256が一致しません。");
            var artifact = StrictUtf8.GetString(artifactBytes);
            return $"ROLE=REVIEWER\nREVIEW_ATTEMPT: {request.Attempt}\nあなたは独立したReviewerです。実装者の会話・自己評価は受け取っていません。以下の固定成果物をレビューしてください。レビュー対象のソースコードや設計書は変更せず、必要な分析資料・提案などがあれば任意の形式・ファイル名で作業ディレクトリに保存できます。最終応答や保存ファイルに特定の成果物形式を求めません。契約に記された作業範囲と許可操作を守ってください。{ScopeBoundaryInstruction}\n任意成果物の保存先: {Path.Combine(request.RunDirectory!, "exchanges", $"reviewer-{request.Attempt}", "workspace", "artifacts")}\n対象SHA-256: {request.ArtifactSha256}\n\n契約:\n{contract}\n\n固定成果物:\n{artifact}";
        }
        var revision = request.ReviewFinding is null ? "" : $"\n前回レビュー指摘:\n{request.ReviewFinding}\n";
        return $"ROLE=IMPLEMENTER\nあなたはImplementerです。契約に記された作業領域と許可操作の範囲内でのみ作業し、外部書込・削除・マージを行わず、変更後の unified diff とテスト結果を最終応答に含めてください。{ScopeBoundaryInstruction}\n\n契約:\n{contract}{revision}";
    }

    private void PrepareEnvironment(ProcessStartInfo start, string profileRoot)
    {
        profileRoot = Path.GetFullPath(profileRoot);
        var inherited = start.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        start.Environment.Clear();
        var allowed = new[] { "PATH", "SystemDrive", "SystemRoot", "WINDIR", "COMSPEC", "PATHEXT", "PROCESSOR_ARCHITECTURE",
            "NUMBER_OF_PROCESSORS", "DOTNET_ROOT", "LANG", "LC_ALL", "SSL_CERT_FILE", "SSL_CERT_DIR",
            "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "TMPDIR",
            "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy" };
        foreach (var name in allowed)
            if (inherited.TryGetValue(name, out var value) && value is not null) start.Environment[name] = value;
        Directory.CreateDirectory(profileRoot);
        start.Environment["CODEX_HOME"] = profileRoot;
    }

    private static IReadOnlyList<string> CollectArtifactReferences(string artifactDirectory, string runDirectory)
    {
        var references = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(artifactDirectory);
        while (pendingDirectories.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Reviewer成果物にsymlinkまたはreparse pointがあります: {Path.GetFileName(entry)}");
                if ((attributes & FileAttributes.Directory) != 0)
                    pendingDirectories.Push(entry);
                else
                    references.Add(Path.GetRelativePath(runDirectory, entry).Replace('\\', '/'));
            }
        }
        return references;
    }

    private static string ResolveArtifactPath(AgentRequest request)
    {
        var artifactPath = Path.GetFullPath(Path.Combine(request.RunDirectory!, request.ArtifactReference!));
        var runRoot = Path.GetFullPath(request.RunDirectory!) + Path.DirectorySeparatorChar;
        if (!artifactPath.StartsWith(runRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("レビュー成果物が実行記録ディレクトリ外を指しています。");
        return artifactPath;
    }

    private static string PrepareCleanGitWorkspace(string workingDirectory)
    {
        var status = RunGit(workingDirectory, "status", "--porcelain", "--untracked-files=all");
        if (status.Length != 0) throw new InvalidOperationException("実Agent開始前の作業領域に既存差分があります。変更を保持して停止しました。");
        return RunGit(workingDirectory, "rev-parse", "HEAD").Trim();
    }

    private static string CaptureChanges(string workingDirectory, string baseCommit)
    {
        var tracked = RunGit(workingDirectory, "diff", "--binary", "--no-ext-diff", baseCommit, "--");
        var untrackedStatus = RunGit(workingDirectory, "status", "--porcelain", "-z", "--untracked-files=all");
        var newFiles = untrackedStatus.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => entry.StartsWith("?? ", StringComparison.Ordinal)).Select(entry => entry[3..]).ToArray();
        var result = new StringBuilder(tracked);
        foreach (var relativePath in newFiles)
        {
            var path = Path.GetFullPath(Path.Combine(workingDirectory, relativePath));
            var workspaceRoot = Path.GetFullPath(workingDirectory) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"未追跡ファイルが作業領域外を指しています: {relativePath}");
            var component = path;
            while (component.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
            {
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"未追跡パスの構成要素がsymlinkまたはreparse pointです: {relativePath}");
                var parent = Path.GetDirectoryName(component);
                if (parent is null || string.Equals(parent + Path.DirectorySeparatorChar, workspaceRoot, StringComparison.OrdinalIgnoreCase))
                {
                    if ((File.GetAttributes(Path.TrimEndingDirectorySeparator(workingDirectory)) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("Git作業領域ルートがsymlinkまたはreparse pointです。");
                    break;
                }
                component = parent;
            }
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 1_000_000 || bytes.Contains((byte)0))
                throw new InvalidOperationException($"未追跡ファイルはテキスト差分として安全にレビューできません: {relativePath}");
            var text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
            var lines = text.Split('\n');
            if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
            result.AppendLine($"diff --git a/{relativePath} b/{relativePath}");
            result.AppendLine("new file mode 100644");
            result.AppendLine("--- /dev/null");
            result.AppendLine($"+++ b/{relativePath}");
            result.AppendLine($"@@ -0,0 +1,{lines.Length} @@");
            foreach (var line in lines) result.Append('+').AppendLine(line);
        }
        if (result.Length == 0) throw new InvalidOperationException("Gitからレビュー可能な変更差分を取得できませんでした。");
        return $"基準commit: {baseCommit}\n\n```diff\n{result}\n```";
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("gitプロセスを開始できませんでした。");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {arguments[0]} の実行に失敗しました: {error.Trim()}");
        return output;
    }
}

/// <summary>Codex CLI起動に必要な実行パスと任意のプロファイルを保持します。</summary>
/// <param name="ExecutablePath">codex CLI実行ファイルの絶対パスです。</param>
/// <param name="ImplementerCodexHome">Implementerで使うCODEX_HOMEです。</param>
/// <param name="ReviewerCodexHome">Reviewerで使うCODEX_HOMEです。</param>
/// <param name="PrefixArguments">ラッパー実行ファイルを使う場合の先行引数です。</param>
internal sealed record CodexCliRunnerOptions(string ExecutablePath, string ImplementerCodexHome,
    string ReviewerCodexHome, IReadOnlyList<string>? PrefixArguments = null,
    Action<Process>? KillProcessForTesting = null, CancellationToken CancellationToken = default)
{
    /// <summary>CLIと、指定または既定のCODEX_HOMEを検証します。</summary>
    /// <returns>検証済み設定です。</returns>
    public CodexCliRunnerOptions Validate()
    {
        if (!Path.IsPathFullyQualified(ExecutablePath) || !File.Exists(ExecutablePath))
            throw new ArgumentException("Codex CLIの実行ファイルには存在する絶対パスが必要です。");
        _ = ResolveCodexHome(ImplementerCodexHome);
        _ = ResolveCodexHome(ReviewerCodexHome);
        return this;
    }

    internal static string ResolveCodexHome(string? requestedHome)
    {
        var home = string.IsNullOrWhiteSpace(requestedHome)
            ? Environment.GetEnvironmentVariable("CODEX_HOME")
            : requestedHome;
        if (string.IsNullOrWhiteSpace(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        if (!Path.IsPathFullyQualified(home))
            throw new ArgumentException("CODEX_HOMEには絶対パスが必要です。");
        return Path.GetFullPath(home);
    }

}

