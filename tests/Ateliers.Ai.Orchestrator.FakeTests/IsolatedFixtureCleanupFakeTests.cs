using System.Diagnostics;
using Ateliers.Ai.Orchestrator.TestFixtures;
using Xunit;

namespace Ateliers.Ai.Orchestrator.FakeTests;

public sealed class IsolatedFixtureCleanupFakeTests
{
    [Fact]
    [Trait("Category", "Fake")]
    public async Task Cleanup_removes_a_read_only_git_object()
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
    [Trait("Category", "Fake")]
    public void Cleanup_rejects_a_reparse_point_ancestor()
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
    [Trait("Category", "Fake")]
    public void Cleanup_removes_a_child_junction_without_following_it()
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
            if (process.ExitCode != 0) throw Xunit.Sdk.SkipException.ForSkip("このWindows環境ではテスト用junctionを作成できません。");
        }
        else
        {
            try { Directory.CreateSymbolicLink(linkPath, targetPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            { throw Xunit.Sdk.SkipException.ForSkip("この環境ではテスト用directory symlinkを作成できません。"); }
        }
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
}
