namespace Ateliers.Ai.Orchestrator.Core;

/// <summary>必須PR公開ゲートを使う実Agentランナーを示す識別境界です。</summary>
public interface IPullRequestWorkflowAgentRunner { }

/// <summary>GitHub提出を行う一実行の固定情報です。</summary>
public sealed record WorkflowPublicationSession(string WorkingDirectory, string Repository, string Branch, string BaseBranch, string BaseSha,
    string GitConfigPath, string GitConfigSha256, string SafeHooksPath);

/// <summary>レビュー前にpushした、コミットと固定差分を表します。</summary>
public sealed record WorkflowPublicationSnapshot(string CommitSha, string DiffSha256, string ArtifactReference);

/// <summary>GitHubに作成し、headとbaseを照合したプルリクエストです。</summary>
public sealed record WorkflowPullRequest(string Url, string HeadSha, string BaseSha, string Branch, string BaseBranch,
    string EvidenceReference);

/// <summary>必須GitHub提出を段階ごとに実行する境界です。</summary>
public interface IWorkflowPublisher
{
    WorkflowPublicationSession Start(TaskContract contract, string runId, CancellationToken stopToken);
    WorkflowPublicationSnapshot PushChanges(WorkflowPublicationSession session, int attempt, string runDirectory,
        string implementerSnapshotReference, string implementerSnapshotSha256, CancellationToken stopToken);
    WorkflowPullRequest CreatePullRequest(WorkflowPublicationSession session, WorkflowPublicationSnapshot snapshot,
        TaskContract contract, string runDirectory, CancellationToken stopToken);
}
