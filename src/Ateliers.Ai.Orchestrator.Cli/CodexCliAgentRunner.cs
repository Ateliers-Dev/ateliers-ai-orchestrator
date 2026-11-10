using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Ateliers.Ai.Orchestrator.Core;

/// <summary>Codex CLIのJSONL実行をCoreのAgentRunner境界へ接続します。</summary>
internal sealed class CodexCliAgentRunner : IAgentRunner
{
    private const string ScopeBoundaryInstruction = "作業範囲はAgentへの指示であり、OSレベルで範囲外アクセスを完全に防ぐ保証ではありません。";
    private readonly CodexCliRunnerOptions options;
    private readonly ConcurrentDictionary<string, string> initialCommits = new(StringComparer.Ordinal);

    /// <summary>CLI実行ファイルと役割別の使い捨てプロファイルを設定します。</summary>
    /// <param name="options">Codex CLIの実行設定です。</param>
    public CodexCliAgentRunner(CodexCliRunnerOptions options) => this.options = options.Validate();

    /// <inheritdoc />
    public AgentResponse Implement(AgentRequest request) => Invoke(request, "workspace-write");

    /// <inheritdoc />
    public AgentResponse Review(AgentRequest request) => Invoke(request, "read-only");

    private AgentResponse Invoke(AgentRequest request, string sandbox) => InvokeAsync(request, sandbox).GetAwaiter().GetResult();

    private async Task<AgentResponse> InvokeAsync(AgentRequest request, string sandbox)
    {
        var role = request.Role == "Reviewer" ? "reviewer" : "implementer";
        var profileRoot = role == "reviewer" ? options.ReviewerCodexHome : options.ImplementerCodexHome;
        var evidenceDirectory = Path.Combine(request.RunDirectory ?? throw new InvalidOperationException("実行記録先が必要です。"),
            "exchanges", $"{role}-{request.Attempt}");
        Directory.CreateDirectory(evidenceDirectory);
        var stdoutPath = Path.Combine(evidenceDirectory, "codex.stdout.jsonl");
        var stderrPath = Path.Combine(evidenceDirectory, "codex.stderr.txt");
        // The approved PoC must not launch an extra `codex --version` process.
        // Version metadata is intentionally recorded as unavailable.
        const string? cliVersion = null;
        string? error = null;
        var timedOut = false;
        var stopRequested = false;
        string? stopReason = null;
        var start = new ProcessStartInfo(options.ExecutablePath)
        {
            WorkingDirectory = role == "implementer" ? request.Contract.WorkingDirectory : Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in options.PrefixArguments ?? []) start.ArgumentList.Add(argument);
        foreach (var argument in new[] { "exec", "--json", "--sandbox", sandbox, "--ask-for-approval", "never", "-" })
            start.ArgumentList.Add(argument);
        PrepareEnvironment(start, profileRoot);
        var prompt = BuildPrompt(request);
        string? reviewWorkspace = null;
        if (role == "implementer" && request.Attempt == 1)
        {
            try { initialCommits[request.RunId] = PrepareCleanGitWorkspace(request.Contract.WorkingDirectory); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            { error = exception.Message; }
        }
        else if (role == "reviewer")
        {
            try { reviewWorkspace = PrepareReviewerWorkspace(request); start.WorkingDirectory = reviewWorkspace; }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
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
            var stdoutTask = PumpStdoutAsync(process, stdoutWriter, text =>
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
            var stderrTask = PumpStderrAsync(process, stderrWriter);
            process.StandardInput.Write(prompt);
            process.StandardInput.Close();
            var timeout = request.RemainingTime is { } remaining && remaining < TimeSpan.FromMinutes(request.Contract.Limits.MaxDurationMinutes)
                ? remaining : TimeSpan.FromMinutes(request.Contract.Limits.MaxDurationMinutes);
            var exitTask = process.WaitForExitAsync();
            var stopTask = Task.Delay(Timeout.InfiniteTimeSpan, options.CancellationToken);
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
            error = exception.Message;
            if (!File.Exists(stderrPath)) File.WriteAllText(stderrPath, error + Environment.NewLine, new UTF8Encoding(false));
            if (process is { HasExited: false })
            {
                killAttempted = true;
                try { (options.KillProcessForTesting ?? (child => child.Kill(entireProcessTree: true)))(process); } catch (Exception killError) { error += $"; 停止要求失敗: {killError.Message}"; }
                try { process.WaitForExit(10_000); processExitObserved = process.HasExited; if (process.HasExited) exitCode = process.ExitCode; }
                catch (InvalidOperationException) { processExitObserved = false; }
            }
        }
        finally { process?.Dispose(); }

        var succeeded = !stopRequested && !timedOut && error is null && terminalEvent == "turn.completed" && exitCode == 0 &&
            !string.IsNullOrWhiteSpace(response.ToString());
        if (!succeeded && error is null)
            error = stopReason == "caller-cancellation" ? "利用者の停止要求を受け付けました。" : timedOut ? "実行時間上限に到達しました。" :
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
        if (reviewWorkspace is not null)
        {
            try { Directory.Delete(reviewWorkspace, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                succeeded = false;
                stopRequested = true;
                stopReason = "review-workspace-cleanup-failed";
                error ??= $"Reviewer作業領域の一時ファイルを削除できませんでした: {exception.Message}";
            }
        }
        var evidence = new AgentExecutionEvidence(processId, threadId, cliVersion, terminalEvent, exitCode, usageJson,
            Path.GetRelativePath(request.RunDirectory!, stdoutPath).Replace('\\', '/'),
            Path.GetRelativePath(request.RunDirectory!, stderrPath).Replace('\\', '/'), timedOut, killAttempted,
            processExitObserved, streamsClosed, stopRequested, stopReason, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))),
            start.WorkingDirectory, request.ArtifactSha256, error);
        return new AgentResponse(content, succeeded, timedOut, SelfAssessment: selfAssessment,
            ExecutionEvidence: evidence, StopRequested: stopRequested);
    }

    private static async Task PumpStdoutAsync(Process process, StreamWriter writer, Action<string> onLine)
    {
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            await writer.WriteLineAsync(line);
            await writer.FlushAsync();
            onLine(line);
        }
    }

    private static async Task PumpStderrAsync(Process process, StreamWriter writer)
    {
        while (await process.StandardError.ReadLineAsync() is { } line)
        {
            await writer.WriteLineAsync(line);
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
            var artifact = File.ReadAllText(artifactPath);
            return $"ROLE=REVIEWER\nREVIEW_ATTEMPT: {request.Attempt}\nあなたは独立したReviewerです。実装者の会話・自己評価は受け取っていません。読み取り専用で、以下の固定成果物だけをレビューし、受け入れ条件に照らした阻害指摘または「レビュー合格:」で始まる結論を返してください。契約に記された作業範囲と許可操作を守ってください。{ScopeBoundaryInstruction}\n対象SHA-256: {request.ArtifactSha256}\n\n契約:\n{contract}\n\n固定成果物:\n{artifact}";
        }
        var revision = request.ReviewFinding is null ? "" : $"\n前回レビュー指摘:\n{request.ReviewFinding}\n";
        return $"ROLE=IMPLEMENTER\nあなたはImplementerです。契約に記された作業領域と許可操作の範囲内でのみ作業し、外部書込・削除・マージを行わず、変更後の unified diff とテスト結果を最終応答に含めてください。{ScopeBoundaryInstruction}\n\n契約:\n{contract}{revision}";
    }

    private void PrepareEnvironment(ProcessStartInfo start, string profileRoot)
    {
        // profile別ディレクトリ作成の直前にも再検証し、パス差替え競合の窓を狭めます。
        // プロセス起動までハンドルを保持しないため、競合を完全には排除できません。
        options.ValidateProfileLocations();
        profileRoot = CodexCliRunnerOptions.ResolveFinalPath(profileRoot)!;
        var inherited = start.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        start.Environment.Clear();
        var allowed = new[] { "PATH", "SystemRoot", "WINDIR", "COMSPEC", "PATHEXT", "PROCESSOR_ARCHITECTURE",
            "NUMBER_OF_PROCESSORS", "DOTNET_ROOT", "LANG", "LC_ALL", "SSL_CERT_FILE", "SSL_CERT_DIR" };
        foreach (var name in allowed)
            if (inherited.TryGetValue(name, out var value) && value is not null) start.Environment[name] = value;
        var local = Path.Combine(profileRoot, "local");
        var roaming = Path.Combine(profileRoot, "roaming");
        var temporary = Path.Combine(profileRoot, "tmp");
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(roaming);
        Directory.CreateDirectory(temporary);
        start.Environment["CODEX_HOME"] = profileRoot;
        start.Environment["HOME"] = profileRoot;
        start.Environment["USERPROFILE"] = profileRoot;
        start.Environment["LOCALAPPDATA"] = local;
        start.Environment["APPDATA"] = roaming;
        start.Environment["TEMP"] = temporary;
        start.Environment["TMP"] = temporary;
        start.Environment["TMPDIR"] = temporary;
    }

    private string PrepareReviewerWorkspace(AgentRequest request)
    {
        var artifactPath = ResolveArtifactPath(request);
        var root = Path.Combine(Path.GetTempPath(), "Ateliers.Orchestrator.Reviewer", $"{request.RunId}-{request.Attempt}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "fixed-artifact.md"), File.ReadAllText(artifactPath), new UTF8Encoding(false));
        return root;
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

/// <summary>Codex CLI起動に必要な実行パスと独立プロファイルを保持します。</summary>
/// <param name="ExecutablePath">codex CLI実行ファイルの絶対パスです。</param>
/// <param name="ImplementerCodexHome">Implementer専用の使い捨てCODEX_HOMEです。</param>
/// <param name="ReviewerCodexHome">Reviewer専用の使い捨てCODEX_HOMEです。</param>
/// <param name="PrefixArguments">ラッパー実行ファイルを使う場合の先行引数です。</param>
internal sealed record CodexCliRunnerOptions(string ExecutablePath, string ImplementerCodexHome,
    string ReviewerCodexHome, IReadOnlyList<string>? PrefixArguments = null,
    Action<Process>? KillProcessForTesting = null, CancellationToken CancellationToken = default)
{
    /// <summary>絶対パスと相互に異なるプロファイルを検証します。</summary>
    /// <returns>検証済み設定です。</returns>
    public CodexCliRunnerOptions Validate()
    {
        if (!Path.IsPathFullyQualified(ExecutablePath) || !File.Exists(ExecutablePath))
            throw new ArgumentException("Codex CLIの実行ファイルには存在する絶対パスが必要です。");
        if (!Path.IsPathFullyQualified(ImplementerCodexHome) || !Path.IsPathFullyQualified(ReviewerCodexHome))
            throw new ArgumentException("役割別CODEX_HOMEには絶対パスが必要です。");
        var implementer = ResolveFinalPath(Path.GetFullPath(ImplementerCodexHome))!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var reviewer = ResolveFinalPath(Path.GetFullPath(ReviewerCodexHome))!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(implementer, reviewer, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("ImplementerとReviewerには別々のCODEX_HOMEが必要です。");
        ValidateProfileLocations();
        ValidateDisposableProfile(implementer);
        ValidateDisposableProfile(reviewer);
        return this;
    }

    internal void ValidateProfileLocations()
    {
        var implementer = ResolveFinalPath(Path.GetFullPath(ImplementerCodexHome))!
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var reviewer = ResolveFinalPath(Path.GetFullPath(ReviewerCodexHome))!
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        RejectReparseAncestors(ImplementerCodexHome);
        RejectReparseAncestors(ReviewerCodexHome);
        if (string.Equals(implementer, reviewer, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("ImplementerとReviewerには別々のCODEX_HOMEが必要です。");
        var defaultHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var activeHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (IsSameOrNested(implementer, defaultHome) || IsSameOrNested(reviewer, defaultHome) ||
            IsSameOrNested(implementer, ResolveFinalPath(ResolveExistingLinks(defaultHome))) ||
            IsSameOrNested(reviewer, ResolveFinalPath(ResolveExistingLinks(defaultHome))) ||
            IsSameOrNested(implementer, activeHome) || IsSameOrNested(reviewer, activeHome) ||
            IsSameOrNested(implementer, ResolveFinalPath(ResolveExistingLinks(activeHome))) ||
            IsSameOrNested(reviewer, ResolveFinalPath(ResolveExistingLinks(activeHome))))
            throw new ArgumentException("ユーザー既定のCODEX_HOMEまたはその配下は使用できません。");
    }

    private static bool IsSameOrNested(string candidate, string? other)
    {
        if (string.IsNullOrWhiteSpace(other)) return false;
        var normalized = Path.GetFullPath(other).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(candidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectReparseAncestors(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException($"使い捨てプロファイルの祖先にsymlinkまたはjunctionは指定できません: {current.FullName}");
            current = current.Parent;
        }
    }

    private static string? ResolveExistingLinks(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var component in full[ current.Length.. ].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (component.Length == 0) continue;
            current = Path.Combine(current, component);
            var directory = new DirectoryInfo(current);
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                current = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ??
                    throw new ArgumentException($"保護対象プロファイルの実体パスを解決できません: {current}");
        }
        return Path.GetFullPath(current);
    }

    internal static string? ResolveFinalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows() || !Directory.Exists(fullPath)) return fullPath;

        using var handle = CreateFileW(fullPath, 0, 0x00000007, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new ArgumentException($"ディレクトリの実体パスを確認できません: {fullPath}", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));

        var buffer = new System.Text.StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
            throw new ArgumentException($"ディレクトリの実体パスを取得できません: {fullPath}", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        if (length >= buffer.Capacity)
        {
            buffer = new System.Text.StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
                throw new ArgumentException($"ディレクトリの実体パスを取得できません: {fullPath}", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }

        var finalPath = buffer.ToString();
        if (finalPath.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            finalPath = "\\\\" + finalPath[8..];
        else if (finalPath.StartsWith("\\\\?\\", StringComparison.Ordinal))
            finalPath = finalPath[4..];
        return Path.GetFullPath(finalPath);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, System.Text.StringBuilder path,
        uint pathLength, uint flags);

    private static void ValidateDisposableProfile(string path)
    {
        var marker = Path.Combine(path, "codex-disposable-profile.json");
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException($"使い捨てプロファイルのルートにsymlinkまたはreparse pointは指定できません: {path}");
            if ((File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException($"使い捨てプロファイル確認ファイルにsymlinkまたはreparse pointは指定できません: {marker}");
            if (Directory.EnumerateFileSystemEntries(path)
                .Any(entry => !string.Equals(Path.GetFileName(entry), Path.GetFileName(marker), StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"確認ファイル以外の内容があるプロファイルは再利用できません: {path}");
            using var document = JsonDocument.Parse(File.ReadAllBytes(marker));
            if (!document.RootElement.TryGetProperty("disposable", out var value) || value.ValueKind != JsonValueKind.True)
                throw new ArgumentException($"使い捨てプロファイル確認ファイルが必要です: {marker}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { throw new ArgumentException($"役割別の使い捨てCodexプロファイルを確認できません: {marker}", exception); }
    }
}
