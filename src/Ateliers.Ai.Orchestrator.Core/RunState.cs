namespace Ateliers.Ai.Orchestrator.Core;

/// <summary>オーケストレーター実行の進行状態を表します。</summary>
public enum RunState
{
    /// <summary>契約検証と実行記録の初期化が完了した状態です。</summary>
    Prepared,
    /// <summary>Implementerが作業中の状態です。</summary>
    Implementing,
    /// <summary>固定した成果物をReviewerが検査中の状態です。</summary>
    Reviewing,
    /// <summary>レビュー指摘を受けてImplementerが修正中の状態です。</summary>
    Revising,
    /// <summary>人間によるPR確認に渡す準備が整った状態です。</summary>
    ReadyForHumanReview,
    /// <summary>実行を止め、人間の判断または対応を待つ状態です。</summary>
    NeedsHumanInput,
    /// <summary>回復できない実行失敗が発生した状態です。</summary>
    Failed
}

/// <summary>状態機械で許可されていない遷移が要求された場合に発生します。</summary>
/// <param name="message">禁止された遷移を説明するメッセージです。</param>
public sealed class InvalidRunTransitionException(string message) : InvalidOperationException(message);

/// <summary>実行状態間の許可された遷移を定義し、禁止遷移を拒否します。</summary>
public static class RunStateMachine
{
    private static readonly IReadOnlyDictionary<RunState, IReadOnlySet<RunState>> Allowed =
        new Dictionary<RunState, IReadOnlySet<RunState>>
        {
            [RunState.Prepared] = new HashSet<RunState> { RunState.Implementing, RunState.NeedsHumanInput, RunState.Failed },
            [RunState.Implementing] = new HashSet<RunState> { RunState.Reviewing, RunState.NeedsHumanInput, RunState.Failed },
            [RunState.Reviewing] = new HashSet<RunState> { RunState.Revising, RunState.NeedsHumanInput, RunState.Failed },
            [RunState.Revising] = new HashSet<RunState> { RunState.Implementing, RunState.NeedsHumanInput, RunState.Failed },
            [RunState.ReadyForHumanReview] = new HashSet<RunState>(),
            [RunState.NeedsHumanInput] = new HashSet<RunState>(),
            [RunState.Failed] = new HashSet<RunState>()
        };

    /// <summary>指定した状態から遷移できる状態を返します。</summary>
    /// <param name="from">遷移元の状態です。</param>
    /// <returns>許可される遷移先の一覧です。</returns>
    public static IReadOnlyCollection<RunState> AllowedTargets(RunState from) => Allowed[from].ToArray();

    /// <summary>指定した状態遷移が許可されていることを確認します。</summary>
    /// <param name="from">遷移元の状態です。</param>
    /// <param name="to">遷移先の状態です。</param>
    /// <exception cref="InvalidRunTransitionException">遷移が状態機械で許可されていない場合に発生します。</exception>
    public static void EnsureAllowed(RunState from, RunState to)
    {
        if (!Allowed[from].Contains(to))
            throw new InvalidRunTransitionException($"状態 {from} から {to} への遷移は禁止されています。");
    }
}
