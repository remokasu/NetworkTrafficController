using Microsoft.Extensions.Logging;
using NetworkTrafficController.Core.Mapping;
using NetworkTrafficController.Core.Monitoring;

namespace NetworkTrafficController.Core.Control;

/// <summary>
/// 制御の司令塔（ADR-0002 制御ハンドル / ADR-0005 パイプライン）。制御受信スレッドとシェーパスレッドを持つ。
/// 受信したパケットを照合表で PID に帰属させ、対象ならポリシーで Pass / Drop / Reset / Delay を決める。
/// 照合できない・対象外・解析できないパケットはすべてそのまま再注入する（fail-open）。
/// loopback は送信側・受信側の規則を直列に適用する（ADR-0003）。
/// </summary>
/// <remarks>
/// フェーズ 3 では、ハンドルを開く時点の対象ローカルポートでフィルタを作り（ADR-0002 B-2）、開き直さない。
/// 待受ポートが固定のサーバーを対象にする場合はこれで全通信を捕捉できる。対象のポートが増減する場合
/// （クライアントの一時ポート等）の開き直し（B-3）は、隙間の扱いと遅延キューの引き継ぎを含めてフェーズ 4 で実装する。
/// 全 TCP/UDP を捕まえる <see cref="ControlFilterBuilder.CatchAll"/> は、他プロセスのパケットもユーザーモードを
/// 経由させるため、呼び出し側が明示的に選んだときだけ使う。
/// </remarks>
public sealed class ControlEngine : IDisposable
{
    private static readonly TimeSpan ShaperMaxWait = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(2);
    private const int MaxConsecutiveReceiveErrors = 100;
    private const int ReceiveBackoffMaxMs = 1000;
    private const int ErrorLogsPerSecond = 5;

    // 受信スレッド専用（LogThrottled は受信スレッドからのみ呼ぶ）
    private long _errorWindowStart;
    private int _errorsInWindow;
    private long _suppressedErrors;
    private long _processingErrors;

    /// <summary>遅延キュー全体のバイト上限（ADR-0005 §7）。帯域キューとは別の安全網で、固定遅延だけのパケットにも効く。</summary>
    public const long MaxQueuedBytes = 16L * 1024 * 1024;

    /// <summary>同じ接続へ RST を注入する最短間隔（ADR-0005 §3）。</summary>
    public static readonly TimeSpan ResetInterval = TimeSpan.FromSeconds(1);

    // 接続ごとの状態（遅延の順序維持・RST の間引き）の上限。超えたら丸ごと捨てる（受信スレッド専用）
    private const int MaxPerConnectionEntries = 16_384;

    private sealed record Pending(ControlPacketToken Token, int Size, IReadOnlyList<(TargetController Controller, Direction Direction)> Reservations);

    private readonly IControlChannel _channel;
    private readonly FlowTable _table;
    private readonly ControlPolicyStore _store;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly DelayQueue<Pending> _queue;

    // 受信スレッド専用: ワイヤ上の 5-tuple（= 接続 × 方向）ごとの最後の解放時刻と最後の RST 時刻
    private readonly Dictionary<FlowKey, long> _lastRelease = new();
    private readonly Dictionary<FlowKey, long> _lastReset = new();
    private long _queuedBytes;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _receiveThread;
    private readonly Thread _shaperThread;
    private int _disposed;
    private long _unparsedPassed;

    /// <summary>統計。</summary>
    public ControlStatistics Statistics { get; }

    /// <summary>解析できずにそのまま通したパケット数。</summary>
    public long UnparsedPassed => Interlocked.Read(ref _unparsedPassed);

    /// <summary>遅延キューに現在あるパケット数。</summary>
    public int QueuedPackets => _queue.Count;

    /// <summary>
    /// 受信ループが致命的なエラーで停止したときに発火する（正常な Dispose では発火しない）。
    /// 通常モードのハンドルは開いている限りパケットを横取りするので、購読側はこのエンジンを Dispose して
    /// ハンドルを閉じ、通信を元に戻すこと（fail-open）。発火はエンジンのスレッドから行うので、購読側は別スレッドで Dispose する。
    /// </summary>
    public event Action? Stopped;

    internal ControlEngine(IControlChannel channel, FlowTable table, ControlPolicyStore store, ControlStatistics statistics, TimeProvider time, ILogger logger)
    {
        _channel = channel;
        _table = table;
        _store = store;
        Statistics = statistics;
        _logger = logger;
        _time = time;
        _queue = new DelayQueue<Pending>(time);
        _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "network-traffic-controller-control-recv" };
        _shaperThread = new Thread(ShaperLoop) { IsBackground = true, Name = "network-traffic-controller-shaper" };
    }

    /// <summary>制御ハンドルのフィルタ文字列。</summary>
    public string Filter { get; private init; } = string.Empty;

    /// <summary>WinDivert の制御ハンドル（通常モード、priority 0）を指定フィルタで開いてエンジンを作る。</summary>
    public static ControlEngine CreateWithWinDivert(string filter, FlowTable table, ControlPolicyStore store, ControlStatistics statistics, TimeProvider time, ILogger logger)
    {
        var channel = WinDivertControlChannel.Open(filter, logger);
        return new ControlEngine(channel, table, store, statistics, time, logger) { Filter = filter };
    }

    /// <summary>受信スレッドとシェーパスレッドを開始する。</summary>
    public void Start()
    {
        _receiveThread.Start();
        _shaperThread.Start();
        _logger.LogInformation("ControlEngine started (priority {Priority}, filter \"{Filter}\")", WinDivertControlChannel.Priority, Filter);
    }

    private void ReceiveLoop()
    {
        int consecutiveReceiveErrors = 0;
        bool fatal = false;
        while (!_cts.IsCancellationRequested)
        {
            ControlPacketToken token;
            try
            {
                if (!_channel.TryReceive(out token))
                {
                    break;
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                // 回復しない受信エラーで空回りしてログを書き続けないよう、バックオフして一定回数で止める
                consecutiveReceiveErrors++;
                LogThrottled(ex, "control receive error (consecutive={Count})", consecutiveReceiveErrors);
                if (consecutiveReceiveErrors >= MaxConsecutiveReceiveErrors)
                {
                    _logger.LogError("Control receive: {Count} consecutive errors, stopping. Firing Stopped so the handle gets closed (otherwise it keeps diverting)", consecutiveReceiveErrors);
                    fatal = true;
                    break;
                }

                _cts.Token.WaitHandle.WaitOne(Math.Min(ReceiveBackoffMaxMs, 1 << Math.Min(consecutiveReceiveErrors, 10)));
                continue;
            }

            consecutiveReceiveErrors = 0;
            try
            {
                Process(token);
            }
            catch (Exception ex)
            {
                // 判定中の例外はそのパケットをそのまま通す（fail-open、ADR-0005 §4）。予約は Process 内で戻している
                Interlocked.Increment(ref _processingErrors);
                LogThrottled(ex, "control processing error; passing packet (total={Count})", Interlocked.Read(ref _processingErrors));
                _channel.Send(token);
            }
        }

        _logger.LogInformation("Control receive loop exited");
        if (fatal && !_cts.IsCancellationRequested)
        {
            // ハンドルを開いたままにしないよう購読側に通知する（購読側は別スレッドで Dispose する）
            Stopped?.Invoke();
        }
    }

    /// <summary>
    /// パケット処理スレッドからのエラーログを 1 秒あたり <see cref="ErrorLogsPerSecond"/> 件に抑える
    /// （同期ファイル書き込みで受信が詰まり、他の通信を遅らせないため）。抑制した件数は次に出すログに添える。
    /// </summary>
    private void LogThrottled(Exception ex, string message, long count)
    {
        long now = Environment.TickCount64;
        if (now - _errorWindowStart >= 1000)
        {
            _errorWindowStart = now;
            _errorsInWindow = 0;
        }

        if (++_errorsInWindow <= ErrorLogsPerSecond)
        {
            _logger.LogError(ex, message + " suppressed={Suppressed}", count, _suppressedErrors);
            _suppressedErrors = 0;
        }
        else
        {
            _suppressedErrors++;
        }
    }

    /// <summary>1 パケットを判定して再注入 / 破棄 / 遅延キューへ振り分ける（テスト用に internal）。</summary>
    internal void Process(ControlPacketToken token)
    {
        var meta = token.Meta;
        if (!PacketParser.TryParse(token.Data.Span, out var packet) || !packet.HasPorts)
        {
            Interlocked.Increment(ref _unparsedPassed);
            _channel.Send(token);
            return;
        }

        int size = token.Data.Length;
        bool isTcp = packet.Protocol == IpProtocol.Tcp;

        // ワイヤ上の向きのままの 5-tuple。同じ接続・同じ方向のパケットで共通になる（遅延の順序維持と RST の間引きに使う）
        var wireKey = new FlowKey(packet.Protocol, packet.Source, packet.SourcePort, packet.Destination, packet.DestinationPort);
        var reservations = new List<(TargetController, Direction)>(2);
        PacketDisposition disposition;
        try
        {
            if (meta.Loopback)
            {
                var a = EvaluateSide(wireKey, Direction.Outbound, size, isTcp, reservations);
                var b = EvaluateSide(wireKey.Reversed, Direction.Inbound, size, isTcp, reservations);
                disposition = Dispositions.Combine(a, b);
            }
            else
            {
                var direction = meta.Outbound ? Direction.Outbound : Direction.Inbound;
                var key = direction == Direction.Outbound ? wireKey : wireKey.Reversed;
                disposition = EvaluateSide(key, direction, size, isTcp, reservations);
            }
        }
        catch
        {
            // 片側で遅延予約した後にもう片側が例外を投げた場合、予約を戻さないとキュー計上が漏れて
            // 対象が恒久的にキュー溢れになる。戻してから呼び出し側（そのまま通す）へ
            UndoReservations(reservations, size);
            throw;
        }

        switch (disposition.Action)
        {
            case PacketActionKind.Pass:
                Statistics.RecordPass(size);
                _channel.Send(token);
                break;
            case PacketActionKind.Drop:
                // 片側が遅延で予約していても全体は破棄になるので、予約を戻す
                UndoReservations(reservations, size);
                Statistics.RecordDrop(disposition.DropReason);
                _channel.Release(token);
                break;
            case PacketActionKind.Reset:
                UndoReservations(reservations, size);
                ResetConnection(token, wireKey);
                break;
            case PacketActionKind.Delay:
                Enqueue(token, size, reservations, wireKey, disposition.ReleaseTimestamp);
                break;
        }
    }

    /// <summary>
    /// 元のパケットを破棄し、その送信元へ RST を注入する。同じ接続（ワイヤ上の向き）には <see cref="ResetInterval"/> に
    /// 1 回だけ注入し、それ以外は無音で破棄する（RST の嵐を避ける）。RST を組み立てられない場合も無音の破棄になる。
    /// </summary>
    private void ResetConnection(ControlPacketToken token, in FlowKey wireKey)
    {
        long now = _time.GetTimestamp();
        bool throttled = _lastReset.TryGetValue(wireKey, out long last) && _time.GetElapsedTime(last, now) < ResetInterval;
        bool injected = false;
        if (!throttled && TcpResetBuilder.TryBuild(token.Data.Span, out var reset))
        {
            TrimIfLarge(_lastReset);
            _lastReset[wireKey] = now;
            injected = _channel.InjectReverse(token, reset); // 実際に送信できたときだけ注入成功として数える
        }

        Statistics.RecordReset(injected);
        _channel.Release(token);
    }

    /// <summary>
    /// 遅延キューへ入れる。同じ接続・同じ方向の中では解放時刻が単調増加になるよう補正して順序を保つ（jitter による入れ替わり防止）。
    /// キュー全体の上限を超える場合は破棄する。キューが閉じていたら即再注入する。
    /// </summary>
    private void Enqueue(ControlPacketToken token, int size, List<(TargetController Controller, Direction Direction)> reservations, in FlowKey wireKey, long release)
    {
        if (Interlocked.Read(ref _queuedBytes) + size > MaxQueuedBytes)
        {
            UndoReservations(reservations, size);
            Statistics.RecordDrop(DropReason.QueueOverflow);
            _channel.Release(token);
            return;
        }

        if (_lastRelease.TryGetValue(wireKey, out long previous) && release <= previous)
        {
            release = previous + 1;
        }

        TrimIfLarge(_lastRelease);
        _lastRelease[wireKey] = release;

        var pending = new Pending(token, size, reservations);
        Interlocked.Add(ref _queuedBytes, size);
        if (_queue.TryEnqueue(pending, release))
        {
            Statistics.RecordDelayEnqueued();
        }
        else
        {
            // 終了処理でキューが閉じた後に届いた。取り残さずにすぐ再注入する（fail-open）
            Release(pending);
        }
    }

    private static void TrimIfLarge(Dictionary<FlowKey, long> map)
    {
        if (map.Count >= MaxPerConnectionEntries)
        {
            map.Clear();
        }
    }

    private static void UndoReservations(List<(TargetController Controller, Direction Direction)> reservations, int size)
    {
        foreach (var (controller, direction) in reservations)
        {
            controller.OnDequeued(direction, size);
        }

        reservations.Clear();
    }

    private PacketDisposition EvaluateSide(in FlowKey key, Direction direction, int size, bool isTcp, List<(TargetController, Direction)> reservations)
    {
        if (!_table.TryResolve(key, out uint pid, out _))
        {
            return PacketDisposition.Pass;
        }

        var controller = _store.GetController(pid);
        if (controller is null)
        {
            return PacketDisposition.Pass;
        }

        var d = controller.Evaluate(direction, size, isTcp);
        if (d.Reserved)
        {
            // 帯域キューに計上したものだけ、解放・破棄時に戻す（固定遅延だけの遅延は計上していない）
            reservations.Add((controller, direction));
        }

        return d;
    }

    private void ShaperLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (_queue.TryDequeueDue(ShaperMaxWait, out var pending))
            {
                Release(pending);
            }
        }

        _logger.LogInformation("Shaper loop exited");
    }

    private void Release(Pending pending)
    {
        foreach (var (controller, direction) in pending.Reservations)
        {
            controller.OnDequeued(direction, pending.Size);
        }

        Interlocked.Add(ref _queuedBytes, -pending.Size);
        Statistics.RecordDelayReinjected(pending.Size);
        _channel.Send(pending.Token);
    }

    /// <summary>
    /// 受信を止め、遅延キューに残ったパケットを期限を待たずにすべて再注入してからハンドルを閉じる
    /// （ユーザーの OFF 操作や終了で通信を落とさないため、ADR-0002 §2）。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            _channel.Shutdown();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "control channel shutdown failed");
        }

        JoinOrWarn(_receiveThread);
        var rest = _queue.CloseAndDrain();
        JoinOrWarn(_shaperThread);

        foreach (var pending in rest)
        {
            Release(pending);
        }

        _logger.LogInformation("ControlEngine disposed (flushed {Count} queued packets)", rest.Count);
        _channel.Dispose();
        _cts.Dispose();
    }

    private void JoinOrWarn(Thread thread)
    {
        // Stopped イベントの購読側がこのエンジンのスレッドから Dispose を呼んだ場合、自スレッドを Join すると
        // デッドロックするので避ける（そのスレッドはこの後すぐ終了する）
        if (thread == Thread.CurrentThread)
        {
            return;
        }

        if (thread.IsAlive && !thread.Join(JoinTimeout))
        {
            // Shutdown(Recv) で受信のブロックが解けていない可能性。ハンドルは続けて閉じる（フェイルセーフ優先）
            _logger.LogWarning("Thread {Name} did not exit within {Timeout}", thread.Name, JoinTimeout);
        }
    }
}
