using NetworkTrafficController.Core.Mapping;

namespace NetworkTrafficController.Core.Control;

/// <summary>
/// 1 つの制御対象（PID または exe 名でまとめた集合）に対する判定。ポリシーと上り/下りのシェーパ状態を持つ。
/// 適用順は Block → Reset → Drop % → 帯域制限 → Delay（ADR-0005 §1）。
/// 判定はロックで直列化する（制御受信スレッドとシェーパスレッドから呼ばれうる）。乱数もロック内でだけ使う。
/// </summary>
public sealed class TargetController
{
    private readonly object _lock = new();
    private readonly ShaperState _outbound;
    private readonly ShaperState _inbound;
    private readonly TimeProvider _time;
    private readonly Random _random;
    private ControlPolicy _policy;

    /// <summary>ポリシー・時間源・乱数（Drop % と jitter 用。テストでは種を固定する）を指定して生成する。</summary>
    public TargetController(ControlPolicy policy, TimeProvider time, Random? random = null)
    {
        _policy = policy;
        _time = time;
        _random = random ?? new Random();
        _outbound = new ShaperState(time);
        _inbound = new ShaperState(time);
    }

    /// <summary>現在のポリシー。</summary>
    public ControlPolicy Policy
    {
        get { lock (_lock) { return _policy; } }
    }

    /// <summary>ポリシーを差し替える。</summary>
    public void SetPolicy(ControlPolicy policy)
    {
        lock (_lock)
        {
            _policy = policy;
        }
    }

    /// <summary>1 パケットの扱いを決める。方向が対象外ならそのまま通す。</summary>
    /// <param name="direction">このパケットが対象プロセスにとって送信か受信か。</param>
    /// <param name="size">パケットのバイト数。</param>
    /// <param name="isTcp">TCP か（Reset は TCP のみ）。</param>
    public PacketDisposition Evaluate(Direction direction, int size, bool isTcp)
    {
        lock (_lock)
        {
            var disposition = EvaluateCore(direction, size, isTcp);
            if (disposition.Action is PacketActionKind.Drop or PacketActionKind.Reset)
            {
                _droppedPackets++;
            }
            else if (disposition.Action == PacketActionKind.Delay)
            {
                _delayedPackets++;
            }

            return disposition;
        }
    }

    /// <summary>
    /// この対象の規則で破棄（Block / Reset / Drop % / 帯域 0 / キュー溢れ）と判定したパケット数。
    /// 監視は制御を通過した後のパケットしか数えないため、止めている対象は監視上 0 に見える。その代わりにこれを表示する。
    /// loopback で送信側・受信側の両方が対象の場合、それぞれの規則の判定を数える。
    /// </summary>
    public long DroppedPackets
    {
        get { lock (_lock) { return _droppedPackets; } }
    }

    /// <summary>この対象の規則で遅延させると判定したパケット数。</summary>
    public long DelayedPackets
    {
        get { lock (_lock) { return _delayedPackets; } }
    }

    private long _droppedPackets;
    private long _delayedPackets;

    // 向きの判定は項目ごと（SPEC-0002）。向きが合わない項目はその項目だけ飛ばし、後段の評価は続ける。
    // 帯域は Upload / Download で向きが決まるので、向きの設定を見ない
    private PacketDisposition EvaluateCore(Direction direction, int size, bool isTcp)
    {
        {
            if (_policy.Block && _policy.BlockDirection.Includes(direction))
            {
                return PacketDisposition.Drop(DropReason.Block);
            }

            // UDP には RST が無いので、Reset ON でもそのまま後段へ（SPEC 決定事項: UDP はそのまま通す）
            if (_policy.Reset && isTcp && _policy.ResetDirection.Includes(direction))
            {
                return PacketDisposition.Reset;
            }

            if (_policy.DropProbability > 0 && _policy.DropDirection.Includes(direction) && _random.NextDouble() < _policy.DropProbability)
            {
                return PacketDisposition.Drop(DropReason.Random);
            }

            var disposition = EvaluateBandwidth(direction, size);
            if (disposition.Action == PacketActionKind.Drop || !_policy.HasDelay || !_policy.DelayDirection.Includes(direction))
            {
                return disposition;
            }

            // 帯域で遅延済みならその解放時刻、そうでなければ今を起点に固定遅延 + 揺らぎを足す
            long baseTimestamp = disposition.Action == PacketActionKind.Delay ? disposition.ReleaseTimestamp : _time.GetTimestamp();
            return PacketDisposition.Delay(baseTimestamp + DelayTicks(), disposition.Reserved);
        }
    }

    /// <summary>遅延させたパケット（帯域キューに計上したもの）を処理し終えたときに呼ぶ。</summary>
    public void OnDequeued(Direction direction, int size)
    {
        lock (_lock)
        {
            (direction == Direction.Outbound ? _outbound : _inbound).OnDequeued(size);
        }
    }

    private PacketDisposition EvaluateBandwidth(Direction direction, int size)
    {
        var limit = _policy.LimitFor(direction);
        if (!limit.HasLimit)
        {
            return PacketDisposition.Pass;
        }

        if (limit.IsStopped)
        {
            return PacketDisposition.Drop(DropReason.BandwidthStopped);
        }

        var shaper = direction == Direction.Outbound ? _outbound : _inbound;
        shaper.Configure(limit.BytesPerSecond);
        return shaper.Evaluate(size);
    }

    /// <summary>固定遅延 + 一様乱数(−jitter, +jitter) をタイムスタンプの目盛りで返す（0 未満は 0）。</summary>
    private long DelayTicks()
    {
        double seconds = _policy.Delay.TotalSeconds;
        if (_policy.Jitter > TimeSpan.Zero)
        {
            seconds += ((_random.NextDouble() * 2) - 1) * _policy.Jitter.TotalSeconds;
        }

        return (long)(Math.Max(0, seconds) * _time.TimestampFrequency);
    }
}

/// <summary>複数の判定を 1 パケットに合成する（loopback で送信側・受信側の規則を直列適用するため）。</summary>
public static class Dispositions
{
    /// <summary>
    /// 破棄 &gt; Reset &gt; 遅延 &gt; 通過。遅延どうしは遅い方の解放時刻を採る。
    /// 片側が無音の破棄（Block 等）なら、もう片側の Reset より優先する（無音で切る指定に RST を混ぜない）。
    /// </summary>
    public static PacketDisposition Combine(PacketDisposition a, PacketDisposition b)
    {
        if (a.Action == PacketActionKind.Drop)
        {
            return a;
        }

        if (b.Action == PacketActionKind.Drop)
        {
            return b;
        }

        if (a.Action == PacketActionKind.Reset)
        {
            return a;
        }

        if (b.Action == PacketActionKind.Reset)
        {
            return b;
        }

        bool aDelay = a.Action == PacketActionKind.Delay;
        bool bDelay = b.Action == PacketActionKind.Delay;
        if (aDelay && bDelay)
        {
            return a.ReleaseTimestamp >= b.ReleaseTimestamp ? a : b;
        }

        if (aDelay)
        {
            return a;
        }

        if (bDelay)
        {
            return b;
        }

        return PacketDisposition.Pass;
    }
}
