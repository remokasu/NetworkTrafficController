namespace NetworkTrafficController.Core.Control;

/// <summary>制御の統計（破棄を理由別に、Reset・遅延・通過・再注入を計上）。スレッド安全（Interlocked）。</summary>
public sealed class ControlStatistics
{
    private long _passedPackets;
    private long _passedBytes;
    private long _droppedBlock;
    private long _droppedStopped;
    private long _droppedQueueOverflow;
    private long _droppedRandom;
    private long _resetInjected;
    private long _resetSuppressed;
    private long _delayedPackets;
    private long _reinjectedBytes;

    /// <summary>そのまま通したパケット数。</summary>
    public long PassedPackets => Interlocked.Read(ref _passedPackets);

    /// <summary>そのまま通したバイト数。</summary>
    public long PassedBytes => Interlocked.Read(ref _passedBytes);

    /// <summary>Block で破棄した数。</summary>
    public long DroppedBlock => Interlocked.Read(ref _droppedBlock);

    /// <summary>帯域 0（停止）で破棄した数。</summary>
    public long DroppedStopped => Interlocked.Read(ref _droppedStopped);

    /// <summary>キュー溢れで破棄した数。</summary>
    public long DroppedQueueOverflow => Interlocked.Read(ref _droppedQueueOverflow);

    /// <summary>Drop % で破棄した数。</summary>
    public long DroppedRandom => Interlocked.Read(ref _droppedRandom);

    /// <summary>RST を注入した数（元のパケットは破棄）。</summary>
    public long ResetInjected => Interlocked.Read(ref _resetInjected);

    /// <summary>Reset 対象だったが、間引き・組み立て不可で RST を注入せずに破棄した数。</summary>
    public long ResetSuppressed => Interlocked.Read(ref _resetSuppressed);

    /// <summary>破棄合計（Reset で破棄した元パケットを含む）。</summary>
    public long DroppedTotal => DroppedBlock + DroppedStopped + DroppedQueueOverflow + DroppedRandom + ResetInjected + ResetSuppressed;

    /// <summary>遅延させたパケット数。</summary>
    public long DelayedPackets => Interlocked.Read(ref _delayedPackets);

    /// <summary>再注入したバイト数（実測値の元）。</summary>
    public long ReinjectedBytes => Interlocked.Read(ref _reinjectedBytes);

    internal void RecordPass(int bytes)
    {
        Interlocked.Increment(ref _passedPackets);
        Interlocked.Add(ref _passedBytes, bytes);
        Interlocked.Add(ref _reinjectedBytes, bytes);
    }

    internal void RecordDrop(DropReason reason)
    {
        switch (reason)
        {
            case DropReason.Block:
                Interlocked.Increment(ref _droppedBlock);
                break;
            case DropReason.BandwidthStopped:
                Interlocked.Increment(ref _droppedStopped);
                break;
            case DropReason.QueueOverflow:
                Interlocked.Increment(ref _droppedQueueOverflow);
                break;
            case DropReason.Random:
                Interlocked.Increment(ref _droppedRandom);
                break;
        }
    }

    internal void RecordReset(bool injected) =>
        Interlocked.Increment(ref injected ? ref _resetInjected : ref _resetSuppressed);

    internal void RecordDelayEnqueued() => Interlocked.Increment(ref _delayedPackets);

    internal void RecordDelayReinjected(int bytes) => Interlocked.Add(ref _reinjectedBytes, bytes);
}
