using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ateliers.Ai.Orchestrator.Core;

namespace Ateliers.Ai.Orchestrator.Cli;

/// <summary>Git CLIとGitHub CLIを使って、専用ブランチへのpushとPR提出を行います。</summary>
public sealed class GitHubWorkflowPublisher : IWorkflowPublisher
{
    private readonly string _safeHooksPath = CreateSafeHooksPath();
    private static readonly Regex SecretPath = new(
        @"(^|/)(\.env($|\.)|\.ssh/|id_rsa($|\.)|id_ed25519($|\.)|secrets?([._-]|$)|credentials?([._-]|$)|.*\.(pem|p12|pfx|key)$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SecretContent = new(
        @"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|\bgh[pousr]_[A-Za-z0-9]{20,}\b|\bgithub_pat_[A-Za-z0-9_]{20,}\b|\bAKIA[0-9A-Z]{16}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <inheritdoc />
    public WorkflowPublicationSession Start(TaskContract contract, string runId)
    {
        var root = Git(contract.WorkingDirectory, "rev-parse", "--show-toplevel").Output.Trim();
        if (!Path.GetFullPath(root).Equals(Path.GetFullPath(contract.WorkingDirectory), PathComparison))
            throw new InvalidOperationException("workingDirectory はGitリポジトリのルートを指定してください。");
        if (!string.IsNullOrWhiteSpace(Git(root, "status", "--porcelain=v1", "--untracked-files=all").Output))
            throw new InvalidOperationException("開始時点で作業ツリーに変更があります。cleanな専用checkoutを用意してください。");
        var configPath = Path.GetFullPath(Git(root, "rev-parse", "--git-path", "config").Output.Trim(), root);
        foreach (var scope in new[] { "--system", "--global", "--local" })
        {
            var configuredFilters = Run("git", root, ["config", scope, "--get-regexp", "^filter\\."], allowExitCodeOne: true);
            if (configuredFilters.ExitCode == 0)
                throw new InvalidOperationException("Git設定に外部filterがあります。安全な専用checkoutで再実行してください。");
        }

        // gh handles API authentication; Git push uses the configured credential helper. Neither output is persisted.
        Gh(root, "auth", "status", "--hostname", "github.com");
        var repo = Gh(root, "repo", "view", "--json", "nameWithOwner,defaultBranchRef",
            "--template", "{{.nameWithOwner}}\t{{.defaultBranchRef.name}}").Output.Trim().Split('\t');
        if (repo.Length != 2 || string.IsNullOrWhiteSpace(repo[0]) || string.IsNullOrWhiteSpace(repo[1]))
            throw new InvalidOperationException("GitHubリポジトリまたは既定ブランチを特定できません。");
        var repository = repo[0];
        var baseBranch = repo[1];
        var remoteUrl = Git(root, "remote", "get-url", "origin").Output.Trim();
        if (!RemoteMatches(remoteUrl, repository))
            throw new InvalidOperationException("originがGitHub CLIで特定したリポジトリと一致しません。");

        Git(root, "fetch", "--no-tags", "origin", $"+refs/heads/{baseBranch}:refs/remotes/origin/{baseBranch}");
        var baseSha = Git(root, "rev-parse", $"refs/remotes/origin/{baseBranch}").Output.Trim();
        var headSha = Git(root, "rev-parse", "HEAD").Output.Trim();
        if (!headSha.Equals(baseSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("checkoutのHEADがGitHub既定ブランチの最新SHAと一致しません。最新の既定ブランチから再実行してください。");

        var safeTaskId = Regex.Replace(contract.TaskId, "[^A-Za-z0-9._-]", "-");
        var branch = $"codex/{safeTaskId}-{runId[..Math.Min(8, runId.Length)]}";
        if (!string.IsNullOrWhiteSpace(Git(root, "ls-remote", "--heads", "origin", $"refs/heads/{branch}").Output))
            throw new InvalidOperationException("実行用ブランチが既にリモートに存在します。別のrunIdで再実行してください。");
        Git(root, "switch", "--create", branch, baseSha);
        return new WorkflowPublicationSession(root, repository, branch, baseBranch, baseSha,
            configPath, HashFile(configPath), _safeHooksPath);
    }

    /// <inheritdoc />
    public WorkflowPublicationSnapshot PushChanges(WorkflowPublicationSession session, int attempt, string runDirectory)
    {
        var root = session.WorkingDirectory;
        EnsureTrustedGitConfig(session);
        var status = Git(root, "status", "--porcelain=v1", "-z", "--untracked-files=all").Output;
        var paths = ParseStatusPaths(status);
        var forbidden = paths.FirstOrDefault(path => SecretPath.IsMatch(path.Replace('\\', '/')));
        if (forbidden is not null)
            throw new InvalidOperationException($"秘密情報を含む可能性があるファイルの変更を検出しました: {Path.GetFileName(forbidden)}");
        if (paths.Count == 0)
            throw new InvalidOperationException("ImplementerがGit変更を作成していません。空の変更はpushしません。");

        var stageArguments = new List<string> { "add", "--all", "--", "." };
        var fullRunPath = Path.GetFullPath(runDirectory);
        var relativeRunPath = Path.GetRelativePath(root, fullRunPath).Replace('\\', '/');
        if (relativeRunPath == ".")
            throw new InvalidOperationException("run保存先をリポジトリcheckoutのルート以外に指定してください。");
        if (!relativeRunPath.Equals("..", StringComparison.Ordinal) && !relativeRunPath.StartsWith("../", StringComparison.Ordinal))
            stageArguments.Add($":(exclude,top,literal){relativeRunPath}");
        Git(root, stageArguments.ToArray());
        var stagedPaths = Git(root, "diff", "--cached", "--name-only", "-z").Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (stagedPaths.Length == 0)
            throw new InvalidOperationException("実行記録を除外した後にcommit対象の変更がありません。");
        forbidden = stagedPaths.FirstOrDefault(path => SecretPath.IsMatch(path.Replace('\\', '/')));
        if (forbidden is not null)
            throw new InvalidOperationException($"秘密情報を含む可能性があるファイルをstageしました: {Path.GetFileName(forbidden)}");
        var stagedDiff = Git(root, "diff", "--cached", "--binary", "--no-ext-diff").Output;
        if (SecretContent.IsMatch(stagedDiff))
            throw new InvalidOperationException("差分に既知形式の秘密情報らしい値を検出しました。内容を確認し、秘密情報を除去してください。");
        Git(root, "diff", "--cached", "--check");
        Git(root, "-c", "core.quotePath=false", "commit", "-m", $"{session.Branch} の成果物 {attempt}");
        var commitSha = Git(root, "rev-parse", "HEAD").Output.Trim();
        Git(root, "push", "--porcelain", "origin", $"HEAD:refs/heads/{session.Branch}");
        var remoteLine = Git(root, "ls-remote", "origin", $"refs/heads/{session.Branch}").Output.Trim();
        var remoteSha = remoteLine.Split('\t')[0];
        if (!remoteSha.Equals(commitSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("push後のリモートhead SHAがローカルコミットと一致しません。");

        var diff = Git(root, "diff", "--binary", "--no-ext-diff", session.BaseSha, commitSha).Output;
        if (string.IsNullOrWhiteSpace(diff))
            throw new InvalidOperationException("pushしたコミットからレビュー対象の差分を取得できません。");
        var relative = Path.Combine("artifacts", $"pushed-diff-{attempt}.patch");
        var path = Path.Combine(runDirectory, relative);
        File.WriteAllText(path, diff, new UTF8Encoding(false));
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        return new WorkflowPublicationSnapshot(commitSha, hash, relative);
    }

    /// <inheritdoc />
    public WorkflowPullRequest CreatePullRequest(WorkflowPublicationSession session, WorkflowPublicationSnapshot snapshot,
        TaskContract contract, string runDirectory)
    {
        var root = session.WorkingDirectory;
        EnsureTrustedGitConfig(session);
        var currentSha = Git(root, "rev-parse", "HEAD").Output.Trim();
        var remoteLine = Git(root, "ls-remote", "origin", $"refs/heads/{session.Branch}").Output.Trim();
        if (!currentSha.Equals(snapshot.CommitSha, StringComparison.OrdinalIgnoreCase) ||
            !remoteLine.Split('\t')[0].Equals(snapshot.CommitSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PR作成前にローカルまたはリモートheadがレビュー済みSHAから変化しました。");

        var baseNow = Git(root, "ls-remote", "origin", $"refs/heads/{session.BaseBranch}").Output.Trim().Split('\t')[0];
        if (!baseNow.Equals(session.BaseSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("実行中にbaseブランチが進みました。最新のbaseから再実行して差分を再レビューしてください。");
        var existing = Gh(root, "pr", "list", "--repo", session.Repository, "--head", session.Branch,
            "--base", session.BaseBranch, "--json", "number", "--template", "{{len .}}").Output.Trim();
        if (existing != "0")
            throw new InvalidOperationException("実行用ブランチに既存のPRがあります。重複作成を避けるため停止しました。");

        var bodyPath = Path.Combine(runDirectory, "pull-request-body.md");
        var body = $"## 概要\n\n{contract.Purpose}\n\n## Orchestrator証跡\n\n- レビュー対象コミット: `{snapshot.CommitSha}`\n- レビュー差分SHA-256: `{snapshot.DiffSha256}`\n- base: `{session.BaseBranch}` (`{session.BaseSha}`)\n- 自動マージは行いません。最終確認とマージは人間が行います。\n";
        File.WriteAllText(bodyPath, body, new UTF8Encoding(false));
        var title = contract.Purpose.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (title.Length > 120) title = title[..120];
        var create = Gh(root, "pr", "create", "--repo", session.Repository, "--head", session.Branch,
            "--base", session.BaseBranch, "--title", title, "--body-file", bodyPath).Output.Trim();
        var expectedPrPath = $"/{session.Repository}/pull/";
        if (!Uri.TryCreate(create, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com" ||
            !url.AbsolutePath.StartsWith(expectedPrPath, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(url.AbsolutePath, $"^{Regex.Escape(expectedPrPath)}\\d+/?$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("GitHub CLIのPR作成結果からGitHub PR URLを確認できません。");

        var evidencePath = Path.Combine(runDirectory, "github-pull-request.json");
        var pendingEvidence = new
        {
            evidenceKind = "github-pull-request-created-unverified",
            repository = session.Repository,
            url = create,
            branch = session.Branch,
            baseBranch = session.BaseBranch,
            headSha = snapshot.CommitSha,
            baseSha = session.BaseSha,
            reviewDiffSha256 = snapshot.DiffSha256,
            merged = false,
            verified = false,
            recordedAtUtc = DateTimeOffset.UtcNow
        };
        File.WriteAllText(evidencePath, JsonSerializer.Serialize(pendingEvidence, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

        var details = Gh(root, "pr", "view", create, "--repo", session.Repository, "--json",
            "url,headRefOid,baseRefOid,headRefName,baseRefName,isDraft,state", "--template",
            "{{.url}}\t{{.headRefOid}}\t{{.baseRefOid}}\t{{.headRefName}}\t{{.baseRefName}}\t{{.isDraft}}\t{{.state}}").Output.Trim().Split('\t');
        if (details.Length != 7 || details[5] != "false" || details[6] != "OPEN" ||
            !string.Equals(details[0], create, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(details[1], "^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(details[2], "^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant) ||
            !details[1].Equals(snapshot.CommitSha, StringComparison.OrdinalIgnoreCase) ||
            !details[2].Equals(session.BaseSha, StringComparison.OrdinalIgnoreCase) ||
            details[3] != session.Branch || details[4] != session.BaseBranch)
            throw new InvalidOperationException("作成したPRのURL・head/base SHAまたはブランチを照合できませんでした。");

        var evidence = new
        {
            evidenceKind = "github-pull-request-created",
            repository = session.Repository,
            url = details[0],
            branch = details[3],
            baseBranch = details[4],
            headSha = details[1],
            baseSha = details[2],
            reviewDiffSha256 = snapshot.DiffSha256,
            reviewArtifactReference = snapshot.ArtifactReference,
            merged = false,
            verified = true,
            verifiedAtUtc = DateTimeOffset.UtcNow
        };
        File.WriteAllText(evidencePath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return new WorkflowPullRequest(details[0], details[1], details[2], details[3], details[4], "github-pull-request.json");
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool RemoteMatches(string remote, string repository)
    {
        var normalized = remote.TrimEnd('/').Replace(".git", "", StringComparison.OrdinalIgnoreCase);
        var suffix = $"github.com/{repository}";
        return normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals($"git@github.com:{repository}", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals($"ssh://git@github.com/{repository}", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ParseStatusPaths(string status)
    {
        var paths = new List<string>();
        foreach (var entry in status.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.Length > 3) paths.Add(entry[3..]);
        }
        return paths;
    }

    private ProcessResult Git(string cwd, params string[] args) => Run("git", cwd,
        ["-c", $"core.hooksPath={_safeHooksPath}", "-c", "commit.gpgsign=false", .. args]);
    private static ProcessResult Gh(string cwd, params string[] args) => Run("gh", cwd, args);

    private static string CreateSafeHooksPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ateliers-orchestrator-hooks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string HashFile(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static void EnsureTrustedGitConfig(WorkflowPublicationSession session)
    {
        if (!File.Exists(session.GitConfigPath) ||
            !HashFile(session.GitConfigPath).Equals(session.GitConfigSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Implementer実行後にローカルGit設定が変更されました。安全のため公開処理を停止しました。");
    }

    private static ProcessResult Run(string executable, string cwd, IReadOnlyList<string> args, bool allowExitCodeOne = false)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = cwd, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new InvalidOperationException();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"必要なコマンド {executable} を起動できません。GitとGitHub CLIの導入・PATHを確認してください。");
        }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(true); } catch (Exception) { }
            throw new InvalidOperationException($"{executable} の処理がタイムアウトしました。認証・ネットワーク・権限を確認してください。");
        }
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0 && !(allowExitCodeOne && process.ExitCode == 1))
            throw new InvalidOperationException($"{executable} の処理に失敗しました（終了コード {process.ExitCode}）。資格情報やリモート権限を確認してください。出力に秘密情報が含まれる可能性があるため記録していません。");
        return new ProcessResult(stdout.Result, stderr.Result, process.ExitCode);
    }

    private sealed record ProcessResult(string Output, string Error, int ExitCode);
}
