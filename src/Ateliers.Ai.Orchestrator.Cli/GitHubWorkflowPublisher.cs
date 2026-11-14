using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ateliers.Ai.Orchestrator.Core;

namespace Ateliers.Ai.Orchestrator.Cli;

/// <summary>Git CLIとGitHub CLIを使って、専用ブランチへのpushとPR提出を行います。</summary>
public sealed class GitHubWorkflowPublisher : IWorkflowPublisher, IDisposable
{
    private readonly string _safeHooksPath = CreateSafeHooksPath();
    private string? _isolatedGitConfigPath;
    private string? _isolatedGitConfigSha256;
    private string? _isolatedGitConfigDirectory;
    private CancellationToken _activeStopToken;
    private static readonly Regex SecretPath = new(
        @"(^|/)(\.env($|\.)|\.ssh/|id_rsa($|\.)|id_ed25519($|\.)|secrets?([._-]|$)|credentials?([._-]|$)|.*\.(pem|p12|pfx|key)$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SecretContent = new(
        @"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|\bgh[pousr]_[A-Za-z0-9]{20,}\b|\bgithub_pat_[A-Za-z0-9_]{20,}\b|\bAKIA[0-9A-Z]{16}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <inheritdoc />
    public WorkflowPublicationSession Start(TaskContract contract, string runId, CancellationToken stopToken)
    {
        _activeStopToken = stopToken;
        stopToken.ThrowIfCancellationRequested();
        _isolatedGitConfigPath = CreateIsolatedGitConfig(contract.WorkingDirectory, stopToken);
        _isolatedGitConfigSha256 = HashFile(_isolatedGitConfigPath);
        var root = Git(contract.WorkingDirectory, "rev-parse", "--show-toplevel").Output.Trim();
        if (!Path.GetFullPath(root).Equals(Path.GetFullPath(contract.WorkingDirectory), PathComparison))
            throw new InvalidOperationException("workingDirectory はGitリポジトリのルートを指定してください。");
        if (!string.IsNullOrWhiteSpace(Git(root, "status", "--porcelain=v1", "--untracked-files=all").Output))
            throw new InvalidOperationException("開始時点で作業ツリーに変更があります。cleanな専用checkoutを用意してください。");
        var configPath = Path.GetFullPath(Git(root, "rev-parse", "--git-path", "config").Output.Trim(), root);
        foreach (var scope in new[] { "--system", "--global", "--local" })
        {
            var configuredFilters = Run("git", root, ["config", scope, "--get-regexp", "^filter\\."],
                allowExitCodeOne: true, stopToken: stopToken);
            if (configuredFilters.ExitCode == 0)
                throw new InvalidOperationException("Git設定に外部filterがあります。安全な専用checkoutで再実行してください。");
        }
        var unsafeLocalConfig = Run("git", root, ["config", "--local", "--get-regexp",
                "^(include|filter\\.|credential\\.helper$|core\\.(sshCommand|fsmonitor)$|url\\..*\\.(insteadOf|pushInsteadOf)$)"],
            allowExitCodeOne: true, isolatedGitConfigPath: _isolatedGitConfigPath, stopToken: stopToken);
        if (!string.IsNullOrWhiteSpace(unsafeLocalConfig.Output))
            throw new InvalidOperationException("実行前のローカルGit設定に外部コマンドや設定includeがあります。安全な専用checkoutで再実行してください。");

        // gh handles API authentication; Git push uses the configured credential helper. Neither output is persisted.
        Gh(root, "auth", "status", "--hostname", "github.com");
        var repo = Gh(root, "repo", "view", "--json", "nameWithOwner,defaultBranchRef",
            "--template", "{{.nameWithOwner}}\t{{.defaultBranchRef.name}}").Output.Trim().Split('\t');
        if (repo.Length != 2 || string.IsNullOrWhiteSpace(repo[0]) || string.IsNullOrWhiteSpace(repo[1]))
            throw new InvalidOperationException("GitHubリポジトリまたは既定ブランチを特定できません。");
        var repository = repo[0];
        var baseBranch = repo[1];
        var remoteUrl = Git(root, "remote", "get-url", "origin").Output.Trim();
        var pushRemoteUrl = Git(root, "remote", "get-url", "--push", "origin").Output.Trim();
        if (!RemoteMatches(remoteUrl, repository) || !RemoteMatches(pushRemoteUrl, repository))
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
    public WorkflowPublicationSnapshot PushChanges(WorkflowPublicationSession session, int attempt, string runDirectory,
        string implementerSnapshotReference, string implementerSnapshotSha256, CancellationToken stopToken)
    {
        var root = session.WorkingDirectory;
        stopToken.ThrowIfCancellationRequested();
        EnsureTrustedGitConfig(session);
        var snapshotPath = ResolveRunArtifact(runDirectory, implementerSnapshotReference);
        var snapshotBytes = File.ReadAllBytes(snapshotPath);
        if (!Convert.ToHexStringLower(SHA256.HashData(snapshotBytes)).Equals(implementerSnapshotSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Implementer終了時点のworkspace snapshot証跡が変更されています。");
        var snapshot = JsonSerializer.Deserialize<CapturedWorkspaceSnapshot>(snapshotBytes)
            ?? throw new InvalidOperationException("Implementer workspace snapshotを読み取れません。");
        if (!snapshot.BaseSha.Equals(Git(root, "rev-parse", "HEAD").Output.Trim(), StringComparison.OrdinalIgnoreCase) || snapshot.Entries.Count == 0)
            throw new InvalidOperationException("Implementer workspace snapshotの基準commitが現在のHEADと一致しないか、差分が空です。");

        var paths = snapshot.Entries.Select(entry => ValidateSnapshotPath(root, entry.Path)).ToArray();
        if (paths.Distinct(StringComparer.Ordinal).Count() != paths.Length)
            throw new InvalidOperationException("Implementer workspace snapshotに重複パスがあります。");
        var currentPaths = CaptureChangedPaths(root, snapshot.BaseSha);
        if (!currentPaths.Order(StringComparer.Ordinal).SequenceEqual(snapshot.Entries.Select(entry => entry.Path).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidOperationException("Implementer終了後にworkspaceの変更パスが変化しました。固定snapshotと一致しません。");
        foreach (var entry in snapshot.Entries)
            VerifyWorkspaceSnapshotEntry(root, entry);
        var forbidden = snapshot.Entries.Select(entry => entry.Path).FirstOrDefault(path => SecretPath.IsMatch(path.Replace('\\', '/')));
        if (forbidden is not null)
            throw new InvalidOperationException($"秘密情報を含む可能性があるファイルの変更を検出しました: {Path.GetFileName(forbidden)}");

        stopToken.ThrowIfCancellationRequested();
        var snapshotIndex = Path.Combine(_isolatedGitConfigDirectory!, $"index-{Guid.NewGuid():N}");
        Git(root, ["read-tree", snapshot.BaseSha], stopToken, indexFile: snapshotIndex);
        foreach (var entry in snapshot.Entries)
        {
            stopToken.ThrowIfCancellationRequested();
            if (entry.Deleted)
            {
                Git(root, ["update-index", "--force-remove", "--", $":(literal){entry.Path}"], stopToken, indexFile: snapshotIndex);
                continue;
            }
            var content = Convert.FromBase64String(entry.ContentBase64 ?? throw new InvalidOperationException("snapshot file contentがありません."));
            if (!Convert.ToHexStringLower(SHA256.HashData(content)).Equals(entry.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException($"snapshot file content hashが一致しません: {entry.Path}");
            var blobSha = Git(root, ["hash-object", "-w", $"--path={entry.Path}", "--stdin"], stopToken, content).Output.Trim();
            var indexInfo = Encoding.UTF8.GetBytes($"{entry.Mode} {blobSha}\t{entry.Path}\0");
            Git(root, ["update-index", "-z", "--index-info"], stopToken, indexInfo, snapshotIndex);
        }
        var stagedPaths = Git(root, ["diff", "--cached", "--name-only", "-z", "--no-renames", snapshot.BaseSha, "--"],
            stopToken, indexFile: snapshotIndex).Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (!stagedPaths.Order(StringComparer.Ordinal).SequenceEqual(snapshot.Entries.Select(entry => entry.Path).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidOperationException("snapshot indexの差分パスがImplementer終了時点のsnapshotと一致しません。");
        foreach (var entry in snapshot.Entries) VerifyStagedSnapshotEntry(root, entry, snapshotIndex, stopToken);
        forbidden = stagedPaths.FirstOrDefault(path => SecretPath.IsMatch(path.Replace('\\', '/')));
        if (forbidden is not null)
            throw new InvalidOperationException($"秘密情報を含む可能性があるファイルをstageしました: {Path.GetFileName(forbidden)}");
        var stagedDiff = Git(root, ["diff", "--cached", "--binary", "--no-ext-diff", snapshot.BaseSha, "--"],
            stopToken, indexFile: snapshotIndex).Output;
        if (SecretContent.IsMatch(stagedDiff))
            throw new InvalidOperationException("差分に既知形式の秘密情報らしい値を検出しました。内容を確認し、秘密情報を除去してください。");
        Git(root, ["diff", "--cached", "--check", snapshot.BaseSha, "--"], stopToken, indexFile: snapshotIndex);
        var expectedTree = Git(root, ["write-tree"], stopToken, indexFile: snapshotIndex).Output.Trim();
        stopToken.ThrowIfCancellationRequested();
        var commitSha = Git(root, ["commit-tree", expectedTree, "-p", snapshot.BaseSha, "-m", $"{session.Branch} の成果物 {attempt}"], stopToken).Output.Trim();
        Git(root, ["update-ref", $"refs/heads/{session.Branch}", commitSha, snapshot.BaseSha], stopToken);
        var committedTree = Git(root, "rev-parse", $"{commitSha}^{{tree}}").Output.Trim();
        if (!committedTree.Equals(expectedTree, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("commit treeがImplementer終了時点のsnapshot indexと一致しません。");
        Git(root, ["read-tree", commitSha], stopToken);
        if (!string.IsNullOrWhiteSpace(Git(root, "status", "--porcelain=v1", "-z", "--untracked-files=all").Output))
            throw new InvalidOperationException("snapshot commit後にworking treeが変化しました。リモートへpushしません。");
        try
        {
            Git(root, ["push", "--no-verify", "--porcelain", "origin", $"HEAD:refs/heads/{session.Branch}"], stopToken);
        }
        catch (OperationCanceledException)
        {
            RecordInterruptedPush(session, commitSha, runDirectory);
            throw;
        }
        var remoteLine = Git(root, ["ls-remote", "origin", $"refs/heads/{session.Branch}"], CancellationToken.None).Output.Trim();
        var remoteSha = remoteLine.Split('\t')[0];
        if (!remoteSha.Equals(commitSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("push後のリモートhead SHAがローカルコミットと一致しません。");

        // Once the remote ref has been verified, finish recording the exact pushed diff even if
        // the caller requested cancellation meanwhile. The workflow checks the token immediately
        // after this method and fails before Reviewer/PR; this command is only local evidence work.
        var diff = Git(root, ["diff", "--binary", "--no-ext-diff", session.BaseSha, commitSha], CancellationToken.None).Output;
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
        TaskContract contract, string runDirectory, CancellationToken stopToken)
    {
        _activeStopToken = stopToken;
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
        var pendingEvidencePath = Path.Combine(runDirectory, "github-pull-request-pending.json");
        var verifiedEvidencePath = Path.Combine(runDirectory, "github-pull-request.json");
        if (File.Exists(verifiedEvidencePath))
            throw new InvalidOperationException("検証済みPR証跡ファイルが既にあります。実行記録を上書きしません。");
        WritePendingPullRequestEvidence(pendingEvidencePath, session, snapshot, "create-request-started", null);
        string create;
        try
        {
            create = Gh(root, "pr", "create", "--repo", session.Repository, "--head", session.Branch,
                "--base", session.BaseBranch, "--title", title, "--body-file", bodyPath).Output.Trim();
        }
        catch (OperationCanceledException)
        {
            ReconcileInterruptedPullRequest(session, snapshot, runDirectory);
            throw;
        }
        var expectedPrPath = $"/{session.Repository}/pull/";
        if (!Uri.TryCreate(create, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com" ||
            !url.AbsolutePath.StartsWith(expectedPrPath, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(url.AbsolutePath, $"^{Regex.Escape(expectedPrPath)}\\d+/?$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("GitHub CLIのPR作成結果からGitHub PR URLを確認できません。");

        WritePendingPullRequestEvidence(pendingEvidencePath, session, snapshot, "create-returned", create);
        string[] details;
        try
        {
            details = Gh(root, "pr", "view", create, "--repo", session.Repository, "--json",
                "url,headRefOid,baseRefOid,headRefName,baseRefName,isDraft,state", "--template",
                "{{.url}}\t{{.headRefOid}}\t{{.baseRefOid}}\t{{.headRefName}}\t{{.baseRefName}}\t{{.isDraft}}\t{{.state}}").Output.Trim().Split('\t');
        }
        catch (OperationCanceledException)
        {
            ReconcileInterruptedPullRequest(session, snapshot, runDirectory, create);
            throw;
        }
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
        PersistVerifiedPullRequestEvidence(runDirectory, evidence, details[0], details[1], details[2]);
        return new WorkflowPullRequest(details[0], details[1], details[2], details[3], details[4], "github-pull-request.json");
    }

    internal static void PersistVerifiedPullRequestEvidence(string runDirectory, object evidence,
        string expectedUrl, string expectedHeadSha, string expectedBaseSha)
    {
        var pendingEvidencePath = Path.Combine(runDirectory, "github-pull-request-pending.json");
        var verifiedEvidencePath = Path.Combine(runDirectory, "github-pull-request.json");
        if (File.Exists(verifiedEvidencePath))
            throw new InvalidOperationException("検証済みPR証跡ファイルが既にあります。実行記録を上書きしません。");
        var temporaryEvidencePath = verifiedEvidencePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryEvidencePath,
                JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temporaryEvidencePath, verifiedEvidencePath);
            using (var savedEvidence = JsonDocument.Parse(File.ReadAllBytes(verifiedEvidencePath)))
            {
                var saved = savedEvidence.RootElement;
                if (saved.GetProperty("evidenceKind").GetString() != "github-pull-request-created" ||
                    saved.GetProperty("verified").ValueKind != JsonValueKind.True ||
                    saved.GetProperty("merged").ValueKind != JsonValueKind.False ||
                    saved.GetProperty("url").GetString() != expectedUrl ||
                    saved.GetProperty("headSha").GetString() != expectedHeadSha ||
                    saved.GetProperty("baseSha").GetString() != expectedBaseSha)
                    throw new InvalidOperationException("保存後のPR証跡を再検証できません。");
            }
            File.Delete(pendingEvidencePath);
        }
        finally
        {
            if (File.Exists(temporaryEvidencePath)) File.Delete(temporaryEvidencePath);
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static bool RemoteMatches(string remote, string repository)
    {
        if (!Regex.IsMatch(repository, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)) return false;
        var expectedPath = $"/{repository}";
        if (Uri.TryCreate(remote, UriKind.Absolute, out var uri))
        {
            if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.Scheme == Uri.UriSchemeHttps &&
                uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
                string.IsNullOrEmpty(uri.Fragment) &&
                (uri.AbsolutePath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) ||
                 uri.AbsolutePath.Equals(expectedPath + ".git", StringComparison.OrdinalIgnoreCase))) return true;
            if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.Scheme == Uri.UriSchemeSsh &&
                uri.UserInfo.Equals("git", StringComparison.Ordinal) && (uri.IsDefaultPort || uri.Port == 22) &&
                string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
                (uri.AbsolutePath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) ||
                 uri.AbsolutePath.Equals(expectedPath + ".git", StringComparison.OrdinalIgnoreCase))) return true;
        }
        return remote.Equals($"git@github.com:{repository}", StringComparison.OrdinalIgnoreCase) ||
            remote.Equals($"git@github.com:{repository}.git", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveRunArtifact(string runDirectory, string reference)
    {
        if (Path.IsPathRooted(reference) || reference.Split(['/', '\\']).Any(part => part is "." or ".."))
            throw new InvalidOperationException("Implementer snapshotのartifact参照が不正です。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runDirectory));
        var path = Path.GetFullPath(Path.Combine(root, reference));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidOperationException("Implementer snapshotがrunディレクトリ外を指しています。");
        return path;
    }

    private void WritePendingPullRequestEvidence(string path, WorkflowPublicationSession session,
        WorkflowPublicationSnapshot snapshot, string outcome, string? url)
    {
        var evidence = new
        {
            evidenceKind = "github-pull-request-create-interrupted-or-pending",
            repository = session.Repository,
            url,
            branch = session.Branch,
            baseBranch = session.BaseBranch,
            expectedHeadSha = snapshot.CommitSha,
            expectedBaseSha = session.BaseSha,
            reviewDiffSha256 = snapshot.DiffSha256,
            outcome,
            verified = false,
            recordedAtUtc = DateTimeOffset.UtcNow
        };
        File.WriteAllText(path, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private void ReconcileInterruptedPullRequest(WorkflowPublicationSession session, WorkflowPublicationSnapshot snapshot,
        string runDirectory, string? createOutputUrl = null)
    {
        var path = Path.Combine(runDirectory, "github-pull-request-pending.json");
        var outcome = "state-unknown";
        string? observedUrl = createOutputUrl;
        try
        {
            var list = Gh(session.WorkingDirectory,
                ["pr", "list", "--repo", session.Repository, "--head", session.Branch, "--base", session.BaseBranch,
                    "--json", "url", "--template", "{{range .}}{{.url}}{{\"\\n\"}}{{end}}"], CancellationToken.None).Output;
            var urls = list.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            observedUrl = urls.Length == 1 ? urls[0] : createOutputUrl;
            if (urls.Length == 0) outcome = "no-matching-pr-observed-after-stop";
            else if (urls.Length > 1) outcome = "multiple-matching-prs-observed-after-stop";
            else
            {
                var details = Gh(session.WorkingDirectory,
                    ["pr", "view", observedUrl!, "--repo", session.Repository, "--json",
                        "headRefOid,baseRefOid,headRefName,baseRefName,isDraft,state", "--template",
                        "{{.headRefOid}}\\t{{.baseRefOid}}\\t{{.headRefName}}\\t{{.baseRefName}}\\t{{.isDraft}}\\t{{.state}}"], CancellationToken.None)
                    .Output.Trim().Split('\t');
                outcome = details.Length == 6 && details[0].Equals(snapshot.CommitSha, StringComparison.OrdinalIgnoreCase) &&
                    details[1].Equals(session.BaseSha, StringComparison.OrdinalIgnoreCase) && details[2] == session.Branch &&
                    details[3] == session.BaseBranch && details[4] == "false" && details[5] == "OPEN"
                    ? "matching-open-pr-confirmed-after-stop" : "matching-branch-pr-found-but-details-unverified";
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            outcome = $"state-unknown:{exception.GetType().Name}";
        }
        WritePendingPullRequestEvidence(path, session, snapshot, outcome, observedUrl);
    }

    private static string ValidateSnapshotPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Any(char.IsControl) ||
            relativePath.Split(['/', '\\']).Any(part => part is "." or "..") ||
            relativePath.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith(".git\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Implementer snapshotに不正なworkspace pathがあります。");
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidOperationException("Implementer snapshot pathがcheckout root外を指しています。");
        return fullPath;
    }

    private HashSet<string> CaptureChangedPaths(string root, string baseSha)
    {
        var paths = new HashSet<string>(Git(root, "diff", "--name-only", "-z", "--no-renames", baseSha, "--")
            .Output.Split('\0', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        foreach (var entry in Git(root, "status", "--porcelain=v1", "-z", "--untracked-files=all").Output
                     .Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(item => item.StartsWith("?? ", StringComparison.Ordinal)))
            paths.Add(entry[3..]);
        return paths;
    }

    private void VerifyWorkspaceSnapshotEntry(string root, CapturedWorkspaceEntry entry)
    {
        var fullPath = ValidateSnapshotPath(root, entry.Path);
        if (entry.Deleted)
        {
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
                throw new InvalidOperationException($"Implementer終了後に削除対象ファイルが再作成されました: {entry.Path}");
            return;
        }
        if (!File.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Implementer終了後にsnapshot対象ファイルが消失または置換されました: {entry.Path}");
        var bytes = File.ReadAllBytes(fullPath);
        if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(entry.Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException($"Implementer終了後にsnapshot対象ファイルが変更されました: {entry.Path}");
    }

    private void VerifyStagedSnapshotEntry(string root, CapturedWorkspaceEntry entry, string indexFile, CancellationToken stopToken)
    {
        var listing = Git(root, ["ls-files", "--stage", "-z", "--", $":(literal){entry.Path}"],
            stopToken, indexFile: indexFile).Output;
        var rows = listing.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (entry.Deleted)
        {
            if (rows.Length != 0)
                throw new InvalidOperationException($"stageに削除対象が残っています: {entry.Path}");
            return;
        }
        if (rows.Length != 1)
            throw new InvalidOperationException($"stage対象blobを一意に確認できません: {entry.Path}");
        var metadata = rows[0].Split('\t', 2)[0].Split(' ');
        if (metadata.Length != 3 || metadata[0] != entry.Mode || metadata[2] != "0")
            throw new InvalidOperationException($"stage済みmodeまたはindex stageがsnapshotと一致しません: {entry.Path}");
        var content = Convert.FromBase64String(entry.ContentBase64 ?? throw new InvalidOperationException("snapshot file contentがありません。"));
        if (!Convert.ToHexStringLower(SHA256.HashData(content)).Equals(entry.Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException($"snapshot file content hashが一致しません: {entry.Path}");
        var expectedBlob = Git(root, ["hash-object", $"--path={entry.Path}", "--stdin"], stopToken, content).Output.Trim();
        if (!metadata[1].Equals(expectedBlob, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"stage済みblobがImplementer終了時点のsnapshotと一致しません: {entry.Path}");
    }

    private void RecordInterruptedPush(WorkflowPublicationSession session, string commitSha, string runDirectory)
    {
        string? observedSha = null;
        var reconciliation = "unknown";
        try
        {
            var remoteLine = Git(session.WorkingDirectory,
                ["ls-remote", "origin", $"refs/heads/{session.Branch}"], CancellationToken.None).Output.Trim();
            observedSha = remoteLine.Split('\t')[0];
            reconciliation = observedSha.Equals(commitSha, StringComparison.OrdinalIgnoreCase) ? "remote-head-matches" : "remote-head-differs";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            reconciliation = $"unknown:{exception.GetType().Name}";
        }
        var evidence = new
        {
            evidenceKind = "github-push-interrupted",
            branch = session.Branch,
            expectedHeadSha = commitSha,
            observedRemoteSha = observedSha,
            reconciliation,
            verified = false,
            recordedAtUtc = DateTimeOffset.UtcNow
        };
        File.WriteAllText(Path.Combine(runDirectory, "github-push-interrupted.json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private sealed record CapturedWorkspaceSnapshot(string BaseSha, IReadOnlyList<CapturedWorkspaceEntry> Entries);
    private sealed record CapturedWorkspaceEntry(string Path, bool Deleted, string Mode, string? Sha256, string? ContentBase64);

    private ProcessResult Git(string cwd, params string[] args) => Git(cwd, args, _activeStopToken);

    private ProcessResult Git(string cwd, string[] args, CancellationToken stopToken, byte[]? standardInput = null,
        string? indexFile = null)
    {
        EnsureSafeGitExecutionContext();
        return Run("git", cwd,
            ["-c", $"core.hooksPath={_safeHooksPath}", "-c", "commit.gpgsign=false", .. args],
            isolatedGitConfigPath: _isolatedGitConfigPath, stopToken: stopToken, standardInput: standardInput,
            isolatedGitIndexPath: indexFile);
    }
    private ProcessResult Gh(string cwd, params string[] args) => Run("gh", cwd, args, stopToken: _activeStopToken);
    private static ProcessResult Gh(string cwd, string[] args, CancellationToken stopToken) => Run("gh", cwd, args, stopToken: stopToken);

    private static string CreateSafeHooksPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ateliers-orchestrator-hooks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        RestrictUnixPermissions(path, directory: true);
        return path;
    }

    private string CreateIsolatedGitConfig(string cwd, CancellationToken stopToken)
    {
        _isolatedGitConfigDirectory = Path.Combine(Path.GetTempPath(), $"ateliers-orchestrator-gitconfig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_isolatedGitConfigDirectory);
        RestrictUnixPermissions(_isolatedGitConfigDirectory, directory: true);
        var configPath = Path.Combine(_isolatedGitConfigDirectory, "config");
        File.WriteAllText(configPath, string.Empty, new UTF8Encoding(false));
        RestrictUnixPermissions(configPath, directory: false);
        // Do not copy user credential-helper commands. GitHub CLI is already authenticated and provides its own helper.
        Run("git", cwd, ["config", "--file", configPath, "--add", "credential.https://github.com.helper", string.Empty], stopToken: stopToken);
        Run("git", cwd, ["config", "--file", configPath, "--add", "credential.https://github.com.helper", "!gh auth git-credential"], stopToken: stopToken);
        foreach (var key in new[] { "user.name", "user.email" })
        {
            var identity = Run("git", cwd, ["config", "--global", "--get", key], allowExitCodeOne: true, stopToken: stopToken);
            if (identity.ExitCode == 0 && !string.IsNullOrWhiteSpace(identity.Output))
                Run("git", cwd, ["config", "--file", configPath, "--add", key, identity.Output.TrimEnd('\r', '\n')], stopToken: stopToken);
        }
        return configPath;
    }

    public void Dispose()
    {
        TryDeleteDirectory(_isolatedGitConfigDirectory);
        TryDeleteDirectory(_safeHooksPath);
        _isolatedGitConfigPath = null;
        _isolatedGitConfigSha256 = null;
        _isolatedGitConfigDirectory = null;
    }

    private static void TryDeleteDirectory(string? directory)
    {
        if (directory is null) return;
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void RestrictUnixPermissions(string path, bool directory)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, directory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string HashFile(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private void EnsureSafeGitExecutionContext()
    {
        if (_isolatedGitConfigPath is null || _isolatedGitConfigSha256 is null ||
            !File.Exists(_isolatedGitConfigPath) ||
            !HashFile(_isolatedGitConfigPath).Equals(_isolatedGitConfigSha256, StringComparison.Ordinal) ||
            !Directory.Exists(_safeHooksPath) || Directory.EnumerateFileSystemEntries(_safeHooksPath).Any())
            throw new InvalidOperationException("隔離したGit設定またはhook無効化領域が変更されています。公開処理を停止しました。");
    }

    private static void EnsureTrustedGitConfig(WorkflowPublicationSession session)
    {
        if (!File.Exists(session.GitConfigPath) ||
            !HashFile(session.GitConfigPath).Equals(session.GitConfigSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Implementer実行後にローカルGit設定が変更されました。安全のため公開処理を停止しました。");
    }

    private static ProcessResult Run(string executable, string cwd, IReadOnlyList<string> args,
        bool allowExitCodeOne = false, string? isolatedGitConfigPath = null,
        CancellationToken stopToken = default, byte[]? standardInput = null, string? isolatedGitIndexPath = null)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = cwd, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = standardInput is not null, CreateNoWindow = true };
        if (isolatedGitConfigPath is not null)
        {
            info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            info.Environment["GIT_CONFIG_GLOBAL"] = isolatedGitConfigPath;
            foreach (var variable in info.Environment.Keys.Where(key => key.StartsWith("GIT_CONFIG_KEY_", StringComparison.OrdinalIgnoreCase) ||
                         key.StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase)).ToArray())
                info.Environment.Remove(variable);
            info.Environment.Remove("GIT_CONFIG_COUNT");
            info.Environment.Remove("GIT_CONFIG_PARAMETERS");
            info.Environment.Remove("GIT_CONFIG_SYSTEM");
            info.Environment.Remove("GIT_INDEX_FILE");
            info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        }
        if (isolatedGitIndexPath is not null) info.Environment["GIT_INDEX_FILE"] = isolatedGitIndexPath;
        foreach (var arg in args) info.ArgumentList.Add(arg);
        stopToken.ThrowIfCancellationRequested();
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
        if (standardInput is not null)
        {
            process.StandardInput.BaseStream.Write(standardInput);
            process.StandardInput.Close();
        }
        try
        {
            process.WaitForExitAsync(stopToken).WaitAsync(TimeSpan.FromSeconds(120)).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            try { process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); } catch (Exception) { }
            throw new OperationCanceledException("外部Git/GitHubコマンド中に停止要求を受け付けました。", stopToken);
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            try { process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); } catch (Exception) { }
            throw new InvalidOperationException($"{executable} の処理がタイムアウトしました。認証・ネットワーク・権限を確認してください。");
        }
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0 && !(allowExitCodeOne && process.ExitCode == 1))
            throw new InvalidOperationException($"{executable} の処理に失敗しました（終了コード {process.ExitCode}）。資格情報やリモート権限を確認してください。出力に秘密情報が含まれる可能性があるため記録していません。");
        return new ProcessResult(stdout.Result, stderr.Result, process.ExitCode);
    }

    private sealed record ProcessResult(string Output, string Error, int ExitCode);
}
