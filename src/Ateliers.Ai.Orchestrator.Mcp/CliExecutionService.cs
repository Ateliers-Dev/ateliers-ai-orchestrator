using System.Diagnostics;
using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;

internal sealed class CliExecutionService
{
    private const int CliProtocolVersion = 1;
    private const int MaxCapturedResponseBytes = 64 * 1024;
    private const int MaxInstallManifestBytes = 16 * 1024;

    public async Task<string> RunAsync(JsonElement contract, string agent, string? codexExecutablePath,
        string? runsDirectory, CancellationToken cancellationToken)
    {
        if (contract.ValueKind != JsonValueKind.Object)
            return Error("invalid_contract_input", "contractにはJSONオブジェクトを指定してください。");
        if (agent is not ("codex" or "fake"))
            return Error("invalid_agent", "agentはcodexまたはfakeを指定してください。");

        if (!TryResolveInstallation(out var cliPath, out var installedVersion, out var resolutionError))
            return Error("cli_not_installed", resolutionError!);

        (bool Supported, string? ErrorCode) capability;
        try { capability = await SupportsStdinProtocolAsync(cliPath, installedVersion, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Error("cli_cancelled", "MCP呼び出しがキャンセルされました。CLI処理の最終状態は不明です。"); }
        if (!capability.Supported)
            return capability.ErrorCode is null
                ? Error("unsupported_cli_protocol", $"インストール済みCLI {installedVersion} はrun stdin JSON protocol v{CliProtocolVersion}に対応していません。対応するRelease CLIを明示的にインストールしてください。")
                : Error(capability.ErrorCode, $"インストール済みRelease CLIを確認できませんでした ({capability.ErrorCode})。");

        var codexPath = codexExecutablePath ?? Environment.GetEnvironmentVariable("ATELIERS_CODEX_CLI_PATH");
        if (agent == "codex" && (string.IsNullOrWhiteSpace(codexPath) || !Path.IsPathFullyQualified(codexPath) || !File.Exists(codexPath)))
            return Error("codex_cli_not_configured", "Codex実行ファイルの絶対パスをcodexExecutablePathまたはATELIERS_CODEX_CLI_PATHで設定してください。");

        if (runsDirectory is not null && !Path.IsPathFullyQualified(runsDirectory))
            return Error("invalid_runs_directory", "runsDirectoryには絶対パスを指定してください。");

        var start = new ProcessStartInfo(cliPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--stdin-json");
        start.ArgumentList.Add("--agent");
        start.ArgumentList.Add(agent);
        if (agent == "codex")
        {
            start.ArgumentList.Add("--codex-exe");
            start.ArgumentList.Add(codexPath!);
        }
        if (runsDirectory is not null)
        {
            start.ArgumentList.Add("--runs");
            start.ArgumentList.Add(runsDirectory);
        }
        SetChildEnvironment(start);

        try
        {
            using var process = new Process { StartInfo = start };
            if (!process.Start()) return Error("cli_start_failed", "Release CLIプロセスを開始できませんでした。");
            try
            {
                var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, MaxCapturedResponseBytes);
                var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, MaxCapturedResponseBytes);
                var contractBytes = Encoding.UTF8.GetBytes(contract.GetRawText());
                await process.StandardInput.BaseStream.WriteAsync(contractBytes, cancellationToken);
                await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
                process.StandardInput.Close();
                await process.WaitForExitAsync(cancellationToken);

                var stdout = await stdoutTask;
                _ = await stderrTask; // Drain child diagnostics without returning potentially sensitive log text to the MCP caller.
                return CreateCliResponse(stdout, process.ExitCode);
            }
            finally
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    if (!process.HasExited) await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or TimeoutException) { }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error("cli_cancelled", "MCP呼び出しがキャンセルされました。CLI処理の最終状態は不明です。");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return Error("invalid_cli_response", "CLIが有効な構造化JSON応答を返しませんでした。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            return Error("cli_execution_failed", $"Release CLIを実行できませんでした ({exception.GetType().Name})。");
        }
    }

    public async Task<string> GetCliResourceAsync(string option, string capability, CancellationToken cancellationToken)
    {
        if (!TryResolveInstallation(out var cliPath, out var installedVersion, out var resolutionError))
            return Error("cli_not_installed", resolutionError!);

        try
        {
            var capabilityStatus = await SupportsCapabilityAsync(cliPath, installedVersion, capability, cancellationToken);
            if (!capabilityStatus.Supported)
                return capabilityStatus.ErrorCode is null
                    ? Error("unsupported_cli_protocol", $"インストール済みCLIは {capability} の取得に対応していません。対応するRelease CLIを明示的にインストールしてください。")
                    : Error(capabilityStatus.ErrorCode, $"インストール済みRelease CLIを確認できませんでした ({capabilityStatus.ErrorCode})。");

            var start = new ProcessStartInfo(cliPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(option);
            SetChildEnvironment(start);
            using var process = new Process { StartInfo = start };
            if (!process.Start()) return Error("cli_start_failed", "Release CLIプロセスを開始できませんでした。");

            var outputTask = ReadBoundedAsync(process.StandardOutput.BaseStream, MaxCapturedResponseBytes);
            var errorTask = ReadBoundedAsync(process.StandardError.BaseStream, 4096);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException) { }
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { }
                return Error(cancellationToken.IsCancellationRequested ? "cli_cancelled" : "cli_resource_timeout",
                    cancellationToken.IsCancellationRequested ? "CLI情報の取得がキャンセルされました。" : "CLI情報の取得がタイムアウトしました。");
            }

            var captured = await outputTask;
            _ = await errorTask;
            if (process.ExitCode != 0)
                return Error("unsupported_cli_protocol", $"インストール済みCLIは {capability} の取得に対応していません。対応するRelease CLIを明示的にインストールしてください。");
            if (captured.Truncated)
                return Error("invalid_cli_response", "CLI情報が上限を超えたため破棄しました。");

            var content = Encoding.UTF8.GetString(captured.Bytes);
            if (string.IsNullOrWhiteSpace(content))
                return Error("invalid_cli_response", "CLIが空の情報を返しました。");
            if (option == "--contract-schema")
            {
                using var json = JsonDocument.Parse(content);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("$schema", out var schemaUri) || schemaUri.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array ||
                    !root.TryGetProperty("$defs", out var definitions) || definitions.ValueKind != JsonValueKind.Object)
                    return Error("invalid_cli_response", "CLIがSchemaオブジェクトを返しませんでした。");
            }
            else if (option == "--orchestration-guide" && !content.TrimStart('\uFEFF', '\r', '\n', ' ').StartsWith("# オーケストレーション契約ガイド", StringComparison.Ordinal))
                return Error("invalid_cli_response", "CLIがオーケストレーションガイドを返しませんでした。");
            return content;
        }
        catch (OperationCanceledException)
        {
            return Error("cli_cancelled", "CLI情報の取得がキャンセルされました。");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return Error("invalid_cli_response", "CLIが有効な情報を返しませんでした。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return Error("cli_start_failed", $"Release CLIを起動できませんでした ({exception.GetType().Name})。");
        }
    }

    public string GetRuntimeDiagnostics()
    {
        var processPath = Environment.ProcessPath;
        string? processFileVersion = null;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            try { processFileVersion = FileVersionInfo.GetVersionInfo(processPath).FileVersion; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception) { }
        }

        var assembly = typeof(CliExecutionService).Assembly;
        var assemblyVersion = assembly.GetName().Version?.ToString();
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return CreateRuntimeDiagnostics(Environment.GetEnvironmentVariable("LOCALAPPDATA"), processPath,
            processFileVersion, assemblyVersion, informationalVersion);
    }

    internal static string CreateRuntimeDiagnostics(string? localAppData, string? processPath,
        string? processFileVersion, string? assemblyVersion, string? informationalVersion)
    {
        string? cliPath = null;
        string? manifestPath = null;
        var localAppDataValid = !string.IsNullOrWhiteSpace(localAppData) && Path.IsPathFullyQualified(localAppData);
        if (localAppDataValid)
        {
            var bin = Path.Combine(localAppData!, "Ateliers-Dev", "Ai-Orchestrator", "bin");
            cliPath = Path.Combine(bin, "Ateliers.Ai.Orchestrator.Cli.exe");
            manifestPath = Path.Combine(bin, "install.json");
        }

        var cliExists = cliPath is not null && File.Exists(cliPath);
        var manifestExists = manifestPath is not null && File.Exists(manifestPath);
        string manifestStatus;
        string? manifestVersion = null;
        string? runtimeIdentifier = null;
        long? manifestBytes = null;
        object? parseError = null;

        if (!localAppDataValid)
        {
            manifestStatus = "localappdata_unavailable";
        }
        else if (!manifestExists)
        {
            manifestStatus = "missing";
        }
        else
        {
            try
            {
                var buffer = new byte[MaxInstallManifestBytes + 1];
                using var stream = new FileStream(manifestPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
                var length = 0;
                while (length < buffer.Length)
                {
                    var read = stream.Read(buffer, length, buffer.Length - length);
                    if (read == 0) break;
                    length += read;
                }
                manifestBytes = stream.Length;
                if (length > MaxInstallManifestBytes)
                {
                    manifestStatus = "too_large";
                }
                else
                {
                    var bytes = buffer.AsSpan(0, length).ToArray();
                    using var manifest = JsonDocument.Parse(bytes);
                    var root = manifest.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        manifestStatus = "invalid_structure";
                    }
                    else
                    {
                        if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String)
                            manifestVersion = version.GetString();
                        if (root.TryGetProperty("runtimeIdentifier", out var rid) && rid.ValueKind == JsonValueKind.String)
                            runtimeIdentifier = rid.GetString();
                        manifestStatus = !string.IsNullOrWhiteSpace(manifestVersion) && runtimeIdentifier == "win-x64"
                            ? "valid"
                            : "invalid_fields";
                    }
                }
            }
            catch (JsonException exception)
            {
                manifestStatus = "invalid_json";
                parseError = new
                {
                    type = exception.GetType().Name,
                    lineNumber = exception.LineNumber,
                    bytePositionInLine = exception.BytePositionInLine
                };
                try { manifestBytes = new FileInfo(manifestPath!).Length; }
                catch (Exception fileException) when (fileException is IOException or UnauthorizedAccessException) { }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                manifestStatus = "unreadable";
                parseError = new { type = exception.GetType().Name };
            }
        }

        return JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            mcp = new
            {
                processPath,
                processFileVersion,
                assemblyVersion,
                informationalVersion
            },
            installation = new
            {
                localAppData,
                localAppDataValid,
                cliPath,
                cliExists,
                manifestPath,
                manifestExists,
                manifestBytes,
                manifestStatus,
                manifestVersion,
                runtimeIdentifier,
                parseError
            }
        });
    }

    private static bool TryResolveInstallation(out string cliPath, out string version, out string? error)
    {
        cliPath = string.Empty;
        version = "unknown";
        error = null;
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData) || !Path.IsPathFullyQualified(localAppData))
        {
            error = "LOCALAPPDATAが設定されていません。CLIインストーラーのユーザー単位配置を確認してください.";
            return false;
        }
        var bin = Path.Combine(localAppData, "Ateliers-Dev", "Ai-Orchestrator", "bin");
        cliPath = Path.Combine(bin, "Ateliers.Ai.Orchestrator.Cli.exe");
        var installManifest = Path.Combine(bin, "install.json");
        if (!File.Exists(cliPath) || !File.Exists(installManifest))
        {
            error = $"インストール済みRelease CLIまたはinstall.jsonがありません: {cliPath}";
            return false;
        }
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(installManifest));
            var root = manifest.RootElement;
            if (!root.TryGetProperty("runtimeIdentifier", out var runtime) || runtime.GetString() != "win-x64" ||
                !root.TryGetProperty("version", out var versionNode) || string.IsNullOrWhiteSpace(versionNode.GetString()))
            {
                error = "install.jsonのCLIバージョンまたはwin-x64 runtimeIdentifierを確認できません。";
                return false;
            }
            version = versionNode.GetString()!;
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
            {
                error = "インストール済みRelease CLIはWindows x64用です。";
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            error = $"install.jsonを読み取れません ({exception.GetType().Name})。";
            return false;
        }
    }

    private static Task<(bool Supported, string? ErrorCode)> SupportsStdinProtocolAsync(string cliPath, string installedVersion, CancellationToken cancellationToken) =>
        SupportsCapabilityAsync(cliPath, installedVersion, "runStdinJson", cancellationToken);

    private static async Task<(bool Supported, string? ErrorCode)> SupportsCapabilityAsync(string cliPath, string installedVersion, string capability, CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo(cliPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("--capabilities");
            SetChildEnvironment(start);
            using var process = new Process { StartInfo = start };
            if (!process.Start()) return (false, "cli_start_failed");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var outputTask = ReadBoundedAsync(process.StandardOutput.BaseStream, 4096);
            var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, 4096);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException) { }
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { }
                if (cancellationToken.IsCancellationRequested) throw;
                return (false, "cli_capabilities_timeout");
            }
            _ = await stderrTask;
            if (process.ExitCode != 0) return (false, null);
            var captured = await outputTask;
            if (captured.Truncated) return (false, null);
            using var json = JsonDocument.Parse(captured.Bytes);
            return (HasCapability(json.RootElement, installedVersion, capability), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException) { return (false, null); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        { return (false, "cli_start_failed"); }
    }

    internal static bool HasCapability(JsonElement root, string installedVersion, string capability)
    {
        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("cliVersion", out var cliVersion) && cliVersion.ValueKind == JsonValueKind.String &&
            string.Equals(NormalizeVersion(cliVersion.GetString()!), NormalizeVersion(installedVersion), StringComparison.OrdinalIgnoreCase) &&
            root.TryGetProperty("protocolVersion", out var protocol) && protocol.ValueKind == JsonValueKind.Number &&
            protocol.TryGetInt32(out var protocolVersion) && protocolVersion == CliProtocolVersion &&
            root.TryGetProperty("commands", out var commands) && commands.ValueKind == JsonValueKind.Object &&
            commands.TryGetProperty(capability, out var supported) && supported.ValueKind == JsonValueKind.True;
    }

    private static string NormalizeVersion(string value) => value.Trim().TrimStart('v', 'V').Split('+', 2)[0];

    private static void SetChildEnvironment(ProcessStartInfo start)
    {
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemDrive", "SystemRoot", "WINDIR", "COMSPEC", "PATHEXT", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "DOTNET_ROOT", "LANG", "LC_ALL", "SSL_CERT_FILE", "SSL_CERT_DIR", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "TMPDIR", "CODEX_HOME", "ATELIERS_CODEX_CLI_PATH", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null) start.Environment[name] = value;
        }
    }

    internal static async Task<CapturedBytes> ReadBoundedAsync(Stream stream, int maxBytes)
    {
        var buffer = new byte[4096];
        using var captured = new MemoryStream(Math.Min(maxBytes, 4096));
        var truncated = false;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            var remaining = maxBytes - (int)captured.Length;
            if (remaining > 0) captured.Write(buffer, 0, Math.Min(read, remaining));
            if (read > remaining) truncated = true;
        }
        return new(captured.ToArray(), truncated);
    }

    internal static string CreateCliResponse(CapturedBytes stdout, int processExitCode)
    {
        if (stdout.Truncated) return Error("cli_response_too_large", "CLI応答が上限を超えたため破棄しました。");
        using var result = JsonDocument.Parse(stdout.Bytes);
        var root = result.RootElement;
        if (!root.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != CliProtocolVersion ||
            !root.TryGetProperty("exitCode", out var exitCode) || exitCode.ValueKind != JsonValueKind.Number || exitCode.GetInt32() != processExitCode ||
            !root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || ok.GetBoolean() != (processExitCode == 0))
            return Error("invalid_cli_response", "CLI応答のprotocolVersionまたは終了コードが一致しません。");
        return root.GetRawText();
    }

    private static string Error(string code, string message) => JsonSerializer.Serialize(new { protocolVersion = 1, ok = false, error = new { code, message } });
    internal sealed record CapturedBytes(byte[] Bytes, bool Truncated);
}
