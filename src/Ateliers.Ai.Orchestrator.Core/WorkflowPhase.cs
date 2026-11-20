using System.Collections.ObjectModel;

namespace Ateliers.Ai.Orchestrator.Core;

/// <summary>Milestone 1で合意した標準ワークフローの段階です。</summary>
public enum WorkflowPhase
{
    /// <summary>テストコードを変更せず、本体機能を実装します。</summary>
    Phase1Implementation = 1,
    /// <summary>本体コードを独立Reviewerが確認します。</summary>
    Phase2ImplementationReview = 2,
    /// <summary>本体コードを変更せず、テストを追加・修正します。</summary>
    Phase3TestChanges = 3,
    /// <summary>設計・本体コード・テストを独立Reviewerが確認します。</summary>
    Phase4FullReview = 4,
    /// <summary>PRのhead/base SHAに対する最終独立レビューを行います。</summary>
    Phase5PullRequestReview = 5
}

/// <summary>Milestone 1の標準フェーズ順と固定再試行上限を定義します。</summary>
public static class WorkflowPhasePolicy
{
    /// <summary>指摘への修正・再レビューを許可する最大回数です（初回レビューを除く）。</summary>
    public const int MaxRevisionAttempts = 3;

    /// <summary>一時的な品質ゲート失敗を同一フェーズで再実行できる最大回数です。</summary>
    public const int MaxTransientGateRetries = 1;

    /// <summary>Run全体で許可する自動差し戻しの最大回数です。</summary>
    public const int MaxRunRollbacks = 6;

    /// <summary>標準ワークフローの次フェーズを返します。最終フェーズの後はnullです。</summary>
    public static WorkflowPhase? Next(WorkflowPhase phase) => phase switch
    {
        WorkflowPhase.Phase1Implementation => WorkflowPhase.Phase2ImplementationReview,
        WorkflowPhase.Phase2ImplementationReview => WorkflowPhase.Phase3TestChanges,
        WorkflowPhase.Phase3TestChanges => WorkflowPhase.Phase4FullReview,
        WorkflowPhase.Phase4FullReview => WorkflowPhase.Phase5PullRequestReview,
        WorkflowPhase.Phase5PullRequestReview => null,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "未知のワークフローフェーズです。")
    };

    /// <summary>独立レビューが必要なフェーズかを返します。</summary>
    public static bool RequiresIndependentReview(WorkflowPhase phase) => phase is
        WorkflowPhase.Phase2ImplementationReview or
        WorkflowPhase.Phase4FullReview or
        WorkflowPhase.Phase5PullRequestReview;

    /// <summary>修正・再レビュー上限が適用されるフェーズかを返します。</summary>
    public static bool HasRevisionLimit(WorkflowPhase phase) => RequiresIndependentReview(phase);
}

/// <summary>Runをまたいで保存する固定再試行カウンターです。</summary>
public sealed record WorkflowRetryCounters
{
    private IReadOnlyDictionary<WorkflowPhase, int> revisionAttempts = Freeze(new Dictionary<WorkflowPhase, int>());
    private IReadOnlyDictionary<WorkflowPhase, int> transientGateRetries = Freeze(new Dictionary<WorkflowPhase, int>());
    /// <summary>修正・再レビューの回数です。レビュー回数上限はPhase 2/4/5に適用します。</summary>
    public IReadOnlyDictionary<WorkflowPhase, int> RevisionAttempts
    {
        get => revisionAttempts;
        init => revisionAttempts = Freeze(value);
    }

    /// <summary>一時的な品質ゲートの再実行回数。フェーズごとに独立します。</summary>
    public IReadOnlyDictionary<WorkflowPhase, int> TransientGateRetries
    {
        get => transientGateRetries;
        init => transientGateRetries = Freeze(value);
    }

    /// <summary>Run全体の自動差し戻し回数です。</summary>
    public int RunRollbacks { get; init; }

    /// <summary>レビュー指摘の修正・再レビューを1回計上した新しいカウンターを返します。</summary>
    public WorkflowRetryCounters RecordRevision(WorkflowPhase phase)
    {
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "未知のワークフローフェーズです。");

        var current = Get(RevisionAttempts, phase);
        if (WorkflowPhasePolicy.HasRevisionLimit(phase) && current >= WorkflowPhasePolicy.MaxRevisionAttempts)
            throw new WorkflowRetryLimitExceededException("revision", phase, WorkflowPhasePolicy.MaxRevisionAttempts);

        return this with { RevisionAttempts = Set(RevisionAttempts, phase, checked(current + 1)) };
    }

    /// <summary>一時的な品質ゲートの再実行を1回計上した新しいカウンターを返します。</summary>
    public WorkflowRetryCounters RecordTransientGateRetry(WorkflowPhase phase, bool sourceChanged)
    {
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "未知のワークフローフェーズです。");

        var current = Get(TransientGateRetries, phase);
        if (current >= WorkflowPhasePolicy.MaxTransientGateRetries)
            throw new WorkflowRetryLimitExceededException("transient_gate_retry", phase, WorkflowPhasePolicy.MaxTransientGateRetries);

        var updated = this with { TransientGateRetries = Set(TransientGateRetries, phase, checked(current + 1)) };
        // コード変更を伴うゲート再試行は、フェーズにかかわらず修正として記録します。
        return sourceChanged ? updated.RecordRevision(phase) : updated;
    }

    /// <summary>Run全体の差し戻しを1回計上した新しいカウンターを返します。</summary>
    public WorkflowRetryCounters RecordRollback()
    {
        if (RunRollbacks >= WorkflowPhasePolicy.MaxRunRollbacks)
            throw new WorkflowRetryLimitExceededException("run_rollback", null, WorkflowPhasePolicy.MaxRunRollbacks);
        return this with { RunRollbacks = RunRollbacks + 1 };
    }

    /// <summary>保存データのカウンター値が許容範囲内か検証します。</summary>
    public void Validate()
    {
        if (RunRollbacks is < 0 or > WorkflowPhasePolicy.MaxRunRollbacks)
            throw new InvalidDataException("保存されたRun差し戻し回数が許容範囲外です。");

        ValidatePhaseCounters(RevisionAttempts, WorkflowPhasePolicy.MaxRevisionAttempts, "修正");
        ValidatePhaseCounters(TransientGateRetries, WorkflowPhasePolicy.MaxTransientGateRetries, "ゲート再試行");
    }

    private static int Get(IReadOnlyDictionary<WorkflowPhase, int> values, WorkflowPhase phase) =>
        values.TryGetValue(phase, out var count) ? count : 0;

    private static IReadOnlyDictionary<WorkflowPhase, int> Set(
        IReadOnlyDictionary<WorkflowPhase, int> values, WorkflowPhase phase, int count)
    {
        var copy = new Dictionary<WorkflowPhase, int>(values) { [phase] = count };
        return copy;
    }

    private static IReadOnlyDictionary<WorkflowPhase, int> Freeze(IReadOnlyDictionary<WorkflowPhase, int> values) =>
        new ReadOnlyDictionary<WorkflowPhase, int>(new Dictionary<WorkflowPhase, int>(values));

    private static void ValidatePhaseCounters(IReadOnlyDictionary<WorkflowPhase, int> values, int maximum, string label)
    {
        foreach (var (phase, count) in values)
        {
            var exceedsMaximum = label == "修正"
                ? WorkflowPhasePolicy.HasRevisionLimit(phase) && count > maximum
                : count > maximum;
            if (!Enum.IsDefined(phase) || count < 0 || exceedsMaximum)
                throw new InvalidDataException($"保存されたフェーズ別{label}回数が許容範囲外です。");
        }
    }
}

/// <summary>合意済みの再試行上限を超えたときに発生します。</summary>
public sealed class WorkflowRetryLimitExceededException(string counter, WorkflowPhase? phase, int maximum)
    : InvalidOperationException($"{counter}の上限 {maximum} 回に達しました。")
{
    /// <summary>上限を超えたカウンター名です。</summary>
    public string Counter { get; } = counter;

    /// <summary>上限を超えたフェーズです。Run全体のカウンターではnullです。</summary>
    public WorkflowPhase? Phase { get; } = phase;

    /// <summary>合意済みの最大回数です。</summary>
    public int Maximum { get; } = maximum;
}
