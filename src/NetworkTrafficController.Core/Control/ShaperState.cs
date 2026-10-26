namespace NetworkTrafficController.Core.Control;

/// <summary>
/// 1 つの (対象, 方向) のシェーピング状態。トークンバケットと遅延キューのバイト残量を持つ。
/// ADR-0005: 容量（バースト）= max(1 パケット最大長, rate×50ms)、キュー上限 = max(64KiB, rate×500ms)。
/// スレッド安全ではない（<see cref="TargetController"/> がロックする）。
/// </summary>
public sealed class ShaperState
{
    private const int PacketMaxLength = 40 + 0xFFFF;
    private const int MinQueueBytes = 64 * 1024;
    private static readonly TimeSpan BurstWindow = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan QueueWindow = TimeSpan.FromMilliseconds(500);

    private readonly TokenBucket _bucket;
    private readonly TimeProvider _time;
    private double _configuredRate = -1;
    private long _queueLimitBytes;

    /// <summary>時間源を指定して生成する（初期はレート未設定）。</summary>
    public ShaperState(TimeProvider time)
    {
        _time = time;
        _bucket = new TokenBucket(0, PacketMaxLength, time);
    }

    /// <summary>遅延キューに入っている（まだ再注入していない）バイト数。</summary>
    public long QueuedBytes { get; private set; }

    /// <summary>現在のキュー上限（バイト）。</summary>
    public long QueueLimitBytes => _queueLimitBytes;

    /// <summary>制限レートに合わせてバケット容量とキュー上限を設定する。</summary>
    public void Configure(double bytesPerSecond)
    {
        if (Math.Abs(bytesPerSecond - _configuredRate) < 0.001)
        {
            return;
        }

        _configuredRate = bytesPerSecond;
        double capacity = Math.Max(PacketMaxLength, bytesPerSecond * BurstWindow.TotalSeconds);
        _bucket.Reconfigure(bytesPerSecond, capacity);
        _queueLimitBytes = (long)Math.Max(MinQueueBytes, bytesPerSecond * QueueWindow.TotalSeconds);
    }

    /// <summary>レート制限下で 1 パケットの扱いを決める。キューに入れる場合はバイトを計上する。</summary>
    public PacketDisposition Evaluate(int size)
    {
        // キュー上限を超えるなら予約せずに破棄する（トークン残高を歪めない）
        if (QueuedBytes + size > _queueLimitBytes)
        {
            return PacketDisposition.Drop(DropReason.QueueOverflow);
        }

        TimeSpan wait = _bucket.Reserve(size);
        if (wait <= TimeSpan.Zero)
        {
            return PacketDisposition.Pass;
        }

        QueuedBytes += size;
        long release = _time.GetTimestamp() + (long)(wait.TotalSeconds * _time.TimestampFrequency);
        return PacketDisposition.Delay(release, reserved: true);
    }

    /// <summary>遅延させたパケットを再注入したとき（またはキューから捨てたとき）に呼び、計上を戻す。</summary>
    public void OnDequeued(int size)
    {
        QueuedBytes = Math.Max(0, QueuedBytes - size);
    }
}
