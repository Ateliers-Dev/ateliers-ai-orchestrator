namespace Ateliers.Ai.Orchestrator.Docker;

/// <summary>一つのRun内で停止要求と外部処理の開始を直列化する最小ゲートです。</summary>
public sealed class RunStartGate
{
    private readonly object sync = new();
    private bool stopRequested;
    private long startSequence;

    /// <summary>停止要求を確定します。確定後は新しい処理を許可しません。</summary>
    public void RequestStop()
    {
        lock (sync) stopRequested = true;
    }

    /// <summary>現在のRunで停止要求が確定したかを返します。</summary>
    public bool IsStopRequested
    {
        get { lock (sync) return stopRequested; }
    }

    /// <summary>処理開始を停止要求と直列化します。</summary>
    public bool TryBeginOperation()
    {
        lock (sync) return !stopRequested;
    }

    /// <summary>連番付き処理開始点を確定します。停止が先ならfalseを返します。</summary>
    public bool TryBeginSequencedStart(out long sequence)
    {
        lock (sync)
        {
            if (stopRequested)
            {
                sequence = startSequence;
                return false;
            }

            sequence = ++startSequence;
            return true;
        }
    }
}
