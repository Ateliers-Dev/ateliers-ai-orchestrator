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

/// <summary>Reviewerが返す承認状態です。</summary>
public enum WorkflowReviewDecision
{
    Approve,
    RequestChanges,
    NeedsHumanInput
}

/// <summary>厳密なヘッダーから解析したReviewerの判定と指摘有無です。</summary>
public sealed record WorkflowReviewAssessment(WorkflowReviewDecision Decision, bool HasFindings, string Summary)
{
    private static readonly string[] FindingFields = ["重要度:", "対象:", "根拠:", "改善案:"];

    /// <summary>Reviewer応答の先頭2行を厳密に解析し、本文の指摘項目とも照合します。</summary>
    public static WorkflowReviewAssessment Parse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            throw new InvalidOperationException("Reviewer応答が空です。");
        var normalized = response.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var decision = lines[0] switch
        {
            "判定: 承認" => WorkflowReviewDecision.Approve,
            "判定: 修正依頼" => WorkflowReviewDecision.RequestChanges,
            "判定: 人間判断" => WorkflowReviewDecision.NeedsHumanInput,
            _ => throw new InvalidOperationException("Reviewer応答の1行目は『判定: 承認』『判定: 修正依頼』『判定: 人間判断』のいずれかである必要があります。")
        };
        var hasFindings = lines.Length > 1 ? lines[1] switch
        {
            "指摘有無: あり" => true,
            "指摘有無: なし" => false,
            _ => throw new InvalidOperationException("Reviewer応答の2行目は『指摘有無: あり』または『指摘有無: なし』である必要があります。")
        } : throw new InvalidOperationException("Reviewer応答に指摘有無の行がありません。");
        if (lines.Length < 3 || lines[2].Length != 0)
            throw new InvalidOperationException("Reviewer応答の判定フィールドの後に空行が必要です。");
        var summary = string.Join('\n', lines.Skip(2)).Trim();
        Validate(decision, hasFindings, summary);
        return new WorkflowReviewAssessment(decision, hasFindings, summary);
    }

    /// <summary>判定と本文中の構造化指摘が一致することを検証します。</summary>
    public static void Validate(WorkflowReviewDecision decision, bool hasFindings, string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            throw new InvalidOperationException("Reviewer応答にレビュー内容がありません。");
        if (decision == WorkflowReviewDecision.RequestChanges && !hasFindings)
            throw new InvalidOperationException("修正依頼には指摘有無: ありが必要です。");
        var summaryLines = summary.Split('\n').Select(line => line.TrimStart())
            .Select(line => line.StartsWith("- ", StringComparison.Ordinal) ? line[2..] : line).ToArray();
        var fieldPresence = FindingFields.Select(field => summaryLines.Any(line =>
            line.StartsWith(field, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(line[field.Length..]))).ToArray();
        if (hasFindings && fieldPresence.Any(present => !present))
            throw new InvalidOperationException("指摘ありのReviewer応答には重要度・対象・根拠・改善案が必要です。");
        if (!hasFindings && fieldPresence.Any(present => present))
            throw new InvalidOperationException("指摘有無: なしとレビュー本文の構造化指摘が一致しません。");
    }
}

/// <summary>PR全体コメントとして記録する独立レビュー結果とレビュー対象の同一性情報です。</summary>
public sealed record WorkflowReviewResult(string RunId, string ReviewerId, string PullRequestUrl, string ReviewedHeadSha,
    string ReviewedBaseSha, string ReviewedBaseBranch, string ReviewDiffSha256, WorkflowReviewDecision Decision,
    bool HasFindings, string Summary);

/// <summary>GitHubへ投稿・照合したレビュー結果コメントです。</summary>
public sealed record WorkflowReviewComment(string Url, long CommentId, string HeadSha, string EvidenceReference);

/// <summary>必須GitHub提出を段階ごとに実行する境界です。</summary>
public interface IWorkflowPublisher
{
    WorkflowPublicationSession Start(TaskContract contract, string runId, CancellationToken stopToken);
    WorkflowPublicationSnapshot PushChanges(WorkflowPublicationSession session, int attempt, string runDirectory,
        string implementerSnapshotReference, string implementerSnapshotSha256, CancellationToken stopToken);
    WorkflowPullRequest CreatePullRequest(WorkflowPublicationSession session, WorkflowPublicationSnapshot snapshot,
        TaskContract contract, string runDirectory, CancellationToken stopToken);
    WorkflowReviewComment PostReviewResultComment(WorkflowPublicationSession session, WorkflowPublicationSnapshot snapshot,
        WorkflowPullRequest pullRequest, WorkflowReviewResult reviewResult, string runDirectory, CancellationToken stopToken);
}
