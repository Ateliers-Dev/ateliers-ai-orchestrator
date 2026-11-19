using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Sdk;

namespace Ateliers.Ai.Orchestrator.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class CodexIntegrationTests
{
    private const string OptInVariable = "ATELIERS_RUN_CODEX_INTEGRATION_TESTS";
    private const string CodexPathVariable = "ATELIERS_CODEX_CLI_PATH";

    [Fact]
    public async Task IsolatedFixture_DisposeRemovesReadOnlyGitObject()
    {
        var fixture = new IsolatedFixture();
        fixture.CreateSeedRepository();

        var objectId = (await RunGitAsync(fixture.RepositoryDirectory, "rev-parse", "HEAD")).Trim();
        var gitObjectPath = Path.Combine(fixture.RepositoryDirectory, ".git", "objects", objectId[..2], objectId[2..]);
        Assert.True(File.Exists(gitObjectPath), "seed commitのGit objectが見つかりません。");
        File.SetAttributes(gitObjectPath, File.GetAttributes(gitObjectPath) | FileAttributes.ReadOnly);

        fixture.Dispose();

        Assert.False(Directory.Exists(fixture.RootDirectory), "ReadOnly Git objectを含むfixtureが残っています。");
    }

    [Fact]
    public void IsolatedFixture_DisposeRejectsReparsePointAncestor()
    {
        var token = Guid.NewGuid().ToString("N");
        var outsideDirectory = Path.Combine(Path.GetTempPath(), $"ateliers-codex-outside-{token}");
        var linkedParent = Path.Combine(Path.GetTempPath(), $"ateliers-codex-fixture-link-{token}");
        var fixture = new IsolatedFixture(linkedParent);
        Directory.CreateDirectory(outsideDirectory);
        var sentinelPath = Path.Combine(outsideDirectory, "keep.txt");
        File.WriteAllText(sentinelPath, "outside fixture");

        try
        {
            CreateDirectoryLink(linkedParent, outsideDirectory);
            fixture.CreateSeedRepository();

            var exception = Assert.Throws<IOException>(() => fixture.Dispose());

            Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sentinelPath), "ancestor junctionのリンク先をcleanupが変更しました。");
            Assert.True(Directory.Exists(fixture.RepositoryDirectory), "リンク先にfixtureデータが残っていません。");
        }
        finally
        {
            if (Directory.Exists(linkedParent)) Directory.Delete(linkedParent, recursive: false);
            if (Directory.Exists(outsideDirectory))
            {
                var outsideFixture = new IsolatedFixture(outsideDirectory, Path.GetFileName(fixture.RootDirectory));
                outsideFixture.Dispose();
                File.Delete(sentinelPath);
                Directory.Delete(outsideDirectory, recursive: false);
            }
        }
    }

    [Fact]
    public void IsolatedFixture_DisposeRemovesChildJunctionWithoutFollowingIt()
    {
        var token = Guid.NewGuid().ToString("N");
        var outsideDirectory = Path.Combine(Path.GetTempPath(), $"ateliers-codex-outside-{token}");
        var fixture = new IsolatedFixture();
        Directory.CreateDirectory(outsideDirectory);
        var sentinelPath = Path.Combine(outsideDirectory, "keep.txt");
        File.WriteAllText(sentinelPath, "outside fixture");

        try
        {
            fixture.CreateSeedRepository();
            CreateDirectoryLink(Path.Combine(fixture.RootDirectory, "outside-link"), outsideDirectory);

            fixture.Dispose();

            Assert.False(Directory.Exists(fixture.RootDirectory), "子junctionを含むfixtureが残っています。");
            Assert.True(File.Exists(sentinelPath), "子junctionのリンク先をcleanupが変更しました。");
        }
        finally
        {
            if (Directory.Exists(fixture.RootDirectory)) fixture.Dispose();
            if (Directory.Exists(outsideDirectory))
            {
                File.Delete(sentinelPath);
                Directory.Delete(outsideDirectory, recursive: false);
            }
        }
    }

    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "/c", "mklink", "/J", linkPath, targetPath }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("junction作成プロセスを開始できませんでした。");
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException("テスト用junctionを作成できませんでした。");
        }
        else
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
    }

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
            var exchangeDirectory = Path.Combine(runDirectory, "exchanges");
            var implementerResponses = Directory.Exists(exchangeDirectory)
                ? Directory.EnumerateDirectories(exchangeDirectory, "implementer-*")
                    .Select(directory => Path.Combine(directory, "response.md"))
                    .Where(File.Exists)
                : [];
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

    private sealed class IsolatedFixture : IDisposable
    {
        private const string FixtureParentName = "ateliers-codex-integration";
        private const uint FileReadAttributes = 0x00000080;
        private const uint FileWriteAttributes = 0x00000100;
        private const uint DeleteAccess = 0x00010000;
        private const uint ShareRead = 0x00000001;
        private const uint ShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint BackupSemantics = 0x02000000;
        private const uint OpenReparsePoint = 0x00200000;
        private const int FileBasicInfoClass = 0;
        private const int FileDispositionInfoClass = 4;
        private const int FileAttributeTagInfoClass = 9;

        public const string TargetFile = "src/WordCounter/TextMetrics.cs";
        public const string SentinelFile = "sentinel.txt";
        public const string SeedSentinel = "do not change\n";

        private readonly string fixtureParentDirectory;

        public IsolatedFixture() : this(Path.Combine(Path.GetTempPath(), FixtureParentName), Guid.NewGuid().ToString("N")) { }

        public IsolatedFixture(string fixtureParentDirectory) : this(fixtureParentDirectory, Guid.NewGuid().ToString("N")) { }

        public IsolatedFixture(string fixtureParentDirectory, string rootDirectoryName)
        {
            this.fixtureParentDirectory = Path.GetFullPath(fixtureParentDirectory);
            RootDirectory = Path.Combine(this.fixtureParentDirectory, rootDirectoryName);
        }

        public string RootDirectory { get; }
        public string RepositoryDirectory => Path.Combine(RootDirectory, "repository");
        public string RunsDirectory => Path.Combine(RootDirectory, "runs");
        public string ContractPath => Path.Combine(RootDirectory, "task-contract.json");

        public void CreateSeedRepository()
        {
            Directory.CreateDirectory(RepositoryDirectory);
            Directory.CreateDirectory(RunsDirectory);
            WriteRepositoryFile(TargetFile, """
                namespace WordCounter;

                public static class TextMetrics
                {
                    public static int CountWords(string text)
                    {
                        ArgumentNullException.ThrowIfNull(text);
                        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                    }
                }
                """);
            WriteRepositoryFile("src/WordCounter/WordCounter.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            WriteRepositoryFile("tests/WordCounter.Tests/WordCounter.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../../src/WordCounter/WordCounter.csproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteRepositoryFile("tests/WordCounter.Tests/Program.cs", """
                using WordCounter;

                var failures = new List<string>();
                Check("ordinary spaces", 3, TextMetrics.CountWords("one two three"));
                Check("repeated spaces", 2, TextMetrics.CountWords("  one   two  "));
                Check("tabs and newlines", 3, TextMetrics.CountWords("one\ttwo\nthree"));
                Check("empty input", 0, TextMetrics.CountWords(""));
                Check("whitespace only", 0, TextMetrics.CountWords(" \t\r\n "));
                foreach (var failure in failures) Console.Error.WriteLine($"FAIL: {failure}");
                if (failures.Count > 0) return 1;
                Console.WriteLine("All word-count checks passed.");
                return 0;

                void Check(string name, int expected, int actual)
                {
                    if (expected != actual) failures.Add($"{name}: expected {expected}, got {actual}");
                }
                """);
            WriteRepositoryFile(".gitignore", """
                **/bin/
                **/obj/
                """);
            File.WriteAllText(Path.Combine(RepositoryDirectory, SentinelFile), SeedSentinel, new UTF8Encoding(false));
            RunGit("init", "--initial-branch=main");
            RunGit("config", "user.name", "Codex Integration Test");
            RunGit("config", "user.email", "integration@example.invalid");
            RunGit("config", "core.autocrlf", "false");
            RunGit("add", "-A");
            RunGit("commit", "-m", "seed integration fixture");
        }

        public void WriteTaskContract()
        {
            var contract = new
            {
                schemaVersion = 1,
                taskId = "codex-integration-test",
                purpose = "TestbedのWordCounter空白区切りシナリオを隔離Git fixtureで修正し、ImplementerのRelease build・5チェック証跡を独立Reviewerへ引き継ぐ",
                workingDirectory = RepositoryDirectory,
                scope = new { included = new[] { TargetFile }, excluded = new[] { SentinelFile, "project/test files", "外部書込・commit・push・PR" } },
                inputs = new[] { new { description = "Testbed由来のWordCounter教材と5チェック", reference = TargetFile, revision = "Testbed main e205cd40166b8e54fd7cb01eb98cc0a57a882426 のシナリオをローカルfixture化" } },
                requirements = new[] { "CountWordsで空白、タブ、改行を区切りとして扱う", "指定のRelease buildと5チェックを実行し、実際のexit codeと各ログを検証ディレクトリのvalidation-evidence.jsonへ記録する", "指定ファイル以外は変更しない", "レビュー後も対象差分を変更しない" },
                acceptanceCriteria = new[] { "Release buildが成功する", "ordinary spaces / repeated spaces / tabs and newlines / empty input / whitespace only の5チェックが成功する", "Reviewerがbuild/testの検証ログを確認し、応答でその結果を明示する", "指定ファイル以外が変更されていない" },
                delegatedOperations = new[] { $"{TargetFile}だけを編集する", "dotnet build src/WordCounter/WordCounter.csproj --configuration Releaseを実行しログを保存する", "dotnet run --project tests/WordCounter.Tests/WordCounter.Tests.csproj --configuration Releaseを実行しログを保存する", "成果物と検証証跡をレビューする" },
                limits = new { maxDurationMinutes = 10, maxRevisionAttempts = 0, maxRetries = 0 },
                stopConditions = new[] { "指定ファイル以外への変更が必要な場合は停止する" },
                reporting = new { pullRequestExpected = false, finalReportFormat = "markdown" }
            };
            File.WriteAllText(ContractPath, JsonSerializer.Serialize(contract, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }

        private void WriteRepositoryFile(string relativePath, string contents)
        {
            var path = Path.Combine(RepositoryDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents, new UTF8Encoding(false));
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
            var fixtureRoot = Path.GetFullPath(RootDirectory);
            if (!Directory.Exists(fixtureRoot)) return;

            var expectedParent = fixtureParentDirectory;
            if (!string.Equals(Path.GetDirectoryName(fixtureRoot), expectedParent, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Fixture cleanup rootが専用一時ディレクトリ直下ではありません: {fixtureRoot}");

            if (OperatingSystem.IsWindows())
            {
                DeleteFixtureTreeWindows(expectedParent, fixtureRoot);
                return;
            }

            ValidateNoReparseAncestors(fixtureRoot);
            var rootAttributes = File.GetAttributes(fixtureRoot);
            if ((rootAttributes & FileAttributes.Directory) == 0 || (rootAttributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Fixture cleanup rootが通常のディレクトリではありません: {fixtureRoot}");
            DeleteFixtureContents(fixtureRoot, fixtureRoot);
            Directory.Delete(fixtureRoot, recursive: false);
        }

        private static void DeleteFixtureTreeWindows(string expectedParent, string fixtureRoot)
        {
            var ancestorHandles = PinDirectoryAncestors(expectedParent);
            try
            {
                using var rootHandle = OpenEntryHandle(fixtureRoot);
                var rootInfo = GetAttributeTagInfo(rootHandle, fixtureRoot);
                if ((rootInfo.FileAttributes & (uint)FileAttributes.Directory) == 0 ||
                    (rootInfo.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Fixture cleanup rootが通常のディレクトリではありません: {fixtureRoot}");

                DeleteFixtureContentsWindows(fixtureRoot, rootHandle);
                MarkForDeletion(rootHandle, fixtureRoot);
            }
            finally
            {
                for (var index = ancestorHandles.Count - 1; index >= 0; index--)
                    ancestorHandles[index].Dispose();
            }
        }

        private static List<SafeFileHandle> PinDirectoryAncestors(string directory)
        {
            var handles = new List<SafeFileHandle>();
            var fullPath = Path.GetFullPath(directory);
            var volumeRoot = Path.GetPathRoot(fullPath) ?? throw new IOException($"Fixture pathにvolume rootがありません: {fullPath}");
            var relativePath = Path.GetRelativePath(volumeRoot, fullPath);
            var currentPath = volumeRoot;

            try
            {
                foreach (var segment in relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
                {
                    currentPath = Path.Combine(currentPath, segment);
                    var handle = OpenDirectoryPin(currentPath);
                    var info = GetAttributeTagInfo(handle, currentPath);
                    if ((info.FileAttributes & (uint)FileAttributes.Directory) == 0 ||
                        (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
                    {
                        handle.Dispose();
                        throw new IOException($"Fixture cleanup ancestorが通常ディレクトリではありません (reparse pointを含む): {currentPath}");
                    }
                    handles.Add(handle);
                }
                return handles;
            }
            catch
            {
                for (var index = handles.Count - 1; index >= 0; index--)
                    handles[index].Dispose();
                throw;
            }
        }

        private static void DeleteFixtureContentsWindows(string directory, SafeFileHandle directoryHandle)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var path = Path.GetFullPath(entry);
                using var entryHandle = OpenEntryHandle(path);
                var info = GetAttributeTagInfo(entryHandle, path);
                var isDirectory = (info.FileAttributes & (uint)FileAttributes.Directory) != 0;
                var isReparsePoint = (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0;

                // The directory handle pins this parent; each child is opened without following reparse points.
                if (isDirectory && !isReparsePoint)
                    DeleteFixtureContentsWindows(path, entryHandle);

                if ((info.FileAttributes & (uint)FileAttributes.ReadOnly) != 0)
                    ClearReadOnlyAttribute(entryHandle, path);

                MarkForDeletion(entryHandle, path);
            }
            GC.KeepAlive(directoryHandle);
        }

        private static SafeFileHandle OpenDirectoryPin(string path)
        {
            return OpenHandle(path, FileReadAttributes);
        }

        private static SafeFileHandle OpenEntryHandle(string path)
        {
            return OpenHandle(path, DeleteAccess | FileReadAttributes | FileWriteAttributes);
        }

        private static SafeFileHandle OpenHandle(string path, uint desiredAccess)
        {
            var handle = CreateFileW(path, desiredAccess, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting,
                BackupSemantics | OpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException($"Fixture cleanupで安全なhandleを開けませんでした: {path}", new Win32Exception(error));
            }
            return handle;
        }

        private static FileAttributeTagInfo GetAttributeTagInfo(SafeFileHandle handle, string path)
        {
            if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out FileAttributeTagInfo info,
                    (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
                throw new IOException($"Fixture cleanupでhandle属性を確認できませんでした: {path}", new Win32Exception(Marshal.GetLastWin32Error()));
            return info;
        }

        private static void ClearReadOnlyAttribute(SafeFileHandle handle, string path)
        {
            if (!GetFileInformationByHandleEx(handle, FileBasicInfoClass, out FileBasicInfo info,
                    (uint)Marshal.SizeOf<FileBasicInfo>()))
                throw new IOException($"Fixture cleanupでhandle属性を読み取れませんでした: {path}", new Win32Exception(Marshal.GetLastWin32Error()));

            info.FileAttributes &= ~(uint)FileAttributes.ReadOnly;
            if (!SetFileInformationByHandle(handle, FileBasicInfoClass, ref info, (uint)Marshal.SizeOf<FileBasicInfo>()))
                throw new IOException($"Fixture cleanupでReadOnly属性を解除できませんでした: {path}", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        private static void MarkForDeletion(SafeFileHandle handle, string path)
        {
            var disposition = new FileDispositionInfo { DeleteFile = true };
            if (!SetFileInformationByHandle(handle, FileDispositionInfoClass, ref disposition, (uint)Marshal.SizeOf<FileDispositionInfo>()))
                throw new IOException($"Fixture cleanupでhandleを削除対象にできませんでした: {path}", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int fileInformationClass,
            out FileAttributeTagInfo fileInformation, uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int fileInformationClass,
            out FileBasicInfo fileInformation, uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass,
            ref FileBasicInfo fileInformation, uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass,
            ref FileDispositionInfo fileInformation, uint bufferSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTagInfo
        {
            public uint FileAttributes;
            public uint ReparseTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileBasicInfo
        {
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public long ChangeTime;
            public uint FileAttributes;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInfo
        {
            [MarshalAs(UnmanagedType.U1)]
            public bool DeleteFile;
        }

        private static void DeleteFixtureContents(string fixtureRoot, string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var path = Path.GetFullPath(entry);
                EnsureInsideFixture(fixtureRoot, path);

                var attributes = File.GetAttributes(path);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;

                // Do not follow links out of the fixture; remove a link as an entry of the fixture.
                if (isDirectory && !isReparsePoint)
                {
                    DeleteFixtureContents(fixtureRoot, path);
                    Directory.Delete(path, recursive: false);
                    continue;
                }

                if (!isReparsePoint && (attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);

                if (isDirectory) Directory.Delete(path, recursive: false);
                else File.Delete(path);
            }
        }

        private static void EnsureInsideFixture(string fixtureRoot, string path)
        {
            var relativePath = Path.GetRelativePath(fixtureRoot, path);
            if (Path.IsPathRooted(relativePath) || relativePath == "." || relativePath == ".." ||
                relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                throw new IOException($"Fixture cleanup pathがfixture rootの外です: {path}");
        }

        private static void ValidateNoReparseAncestors(string fixtureRoot)
        {
            var volumeRoot = Path.GetPathRoot(fixtureRoot) ?? throw new IOException($"Fixture pathにvolume rootがありません: {fixtureRoot}");
            var relativePath = Path.GetRelativePath(volumeRoot, fixtureRoot);
            var currentPath = volumeRoot;
            foreach (var segment in relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                currentPath = Path.Combine(currentPath, segment);
                if (!Directory.Exists(currentPath)) continue;
                var attributes = File.GetAttributes(currentPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Fixture cleanup ancestorがreparse pointです: {currentPath}");
            }
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
