using Microsoft.Extensions.Logging;
using NetworkTrafficController.Core.Mapping;
using NetworkTrafficController.Core.Native;

namespace NetworkTrafficController.Core.Monitoring;

/// <summary><see cref="MonitorEngine"/> の設定。</summary>
public sealed class MonitorEngineOptions
{
    /// <summary>既存接続スナップショットの取得間隔（ADR-0001、既定 5 秒）。</summary>
    public TimeSpan SnapshotInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>更新のない接続を集計から消すまでの時間（ADR-0005、既定 30 秒 + 猶予）。</summary>
    public TimeSpan ConnectionIdleTimeout { get; init; } = TimeSpan.FromSeconds(40);

    /// <summary>接続がなくなったプロセスを集計から消すまでの時間（既定 60 秒）。</summary>
    public TimeSpan ProcessIdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>FLOW イベントを 1 件ずつログに出す（バイトオーダー等の実機検証用）。</summary>
    public bool LogFlowEvents { get; init; }
}

/// <summary>
/// 監視の司令塔。起動順は FLOW ハンドル → スナップショット → 監視ハンドル（ADR-0001 (3)）。
/// FLOW 受信スレッド・監視受信スレッド・スナップショットタイマを持ち、Dispose で全て閉じる。
/// </summary>
public sealed class MonitorEngine : IDisposable
{
    private const int ReceiveBufferSize = WinDivertConstants.MtuMax * 4;
    private const int MaxConsecutiveErrors = 100;
    private const int ErrorLogEvery = 10;
    private const int ErrorBackoffBaseMs = 1;
    private const int ErrorBackoffMaxMs = 1000;

    private readonly IFlowEventSource _flowSource;
    private readonly IPacketSource _packetSource;
    private readonly IConnectionSnapshotProvider _snapshots;
    private readonly MonitorEngineOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _flowThread;
    private readonly Thread _packetThread;
    private readonly Thread _snapshotThread;
    private long _flowEvents;
    private long _packetsReceived;
    private long _batchesReceived;
    private int _disposed;

    /// <summary>照合表（読み取り専用の参照。制御側も同じ表を使う）。</summary>
    public FlowTable FlowTable { get; }

    /// <summary>集計。</summary>
    public MonitorAggregator Aggregator { get; }

    /// <summary>受信した FLOW イベント数。</summary>
    public long FlowEventCount => Interlocked.Read(ref _flowEvents);

    /// <summary>受信したパケット数。</summary>
    public long PacketCount => Interlocked.Read(ref _packetsReceived);

    /// <summary>受信したバッチ数（1 バッチあたりの平均パケット数 = 負荷の目安）。</summary>
    public long BatchCount => Interlocked.Read(ref _batchesReceived);

    /// <summary>供給元と照合表・集計を組み立てる。ハンドルは呼び出し側が開いて渡す（起動順を守るため）。</summary>
    public MonitorEngine(
        IFlowEventSource flowSource,
        IPacketSource packetSource,
        IConnectionSnapshotProvider snapshots,
        FlowTable flowTable,
        MonitorAggregator aggregator,
        MonitorEngineOptions options,
        TimeProvider time,
        ILogger logger)
    {
        _flowSource = flowSource;
        _packetSource = packetSource;
        _snapshots = snapshots;
        FlowTable = flowTable;
        Aggregator = aggregator;
        _options = options;
        _time = time;
        _logger = logger;
        _flowThread = new Thread(FlowLoop) { IsBackground = true, Name = "network-traffic-controller-flow-recv" };
        _packetThread = new Thread(PacketLoop) { IsBackground = true, Name = "network-traffic-controller-monitor-recv" };
        _snapshotThread = new Thread(SnapshotLoop) { IsBackground = true, Name = "network-traffic-controller-snapshot" };
    }

    /// <summary>初回スナップショットを取り込み、各スレッドを開始する。</summary>
    public void Start()
    {
        ApplySnapshot();
        _flowThread.Start();
        _packetThread.Start();
        _snapshotThread.Start();
        _logger.LogInformation("MonitorEngine started: flowTable exact={Exact} endpoints={Endpoints}", FlowTable.ExactCount, FlowTable.EndpointCount);
    }

    /// <summary>スナップショットを取り直して照合表に反映し、期限切れを掃除する。</summary>
    public void ApplySnapshot()
    {
        try
        {
            var tcp = _snapshots.GetTcpConnections();
            var udp = _snapshots.GetUdpEndpoints();
            FlowTable.ApplySnapshot(tcp, udp);
            int swept = FlowTable.Sweep();
            int expired = Aggregator.Expire(_options.ConnectionIdleTimeout, _options.ProcessIdleTimeout);
            _logger.LogDebug("Snapshot applied: tcp={Tcp} udp={Udp} exact={Exact} endpoints={Endpoints} swept={Swept} expired={Expired}", tcp.Count, udp.Count, FlowTable.ExactCount, FlowTable.EndpointCount, swept, expired);
        }
        catch (Exception ex)
        {
            // スナップショットの失敗は監視を止めない（次回に再試行）
            _logger.LogError(ex, "Connection snapshot failed");
        }
    }

    /// <summary>受信ループの連続エラー処理。回復しないエラーで高速に空回りしてログを書き続けないようにする。</summary>
    /// <returns>ループを抜けるべきなら true。</returns>
    private bool HandleLoopError(Exception ex, string loop, ref int consecutiveErrors)
    {
        if (ex is ObjectDisposedException)
        {
            _logger.LogInformation("{Loop}: handle disposed, exiting", loop);
            return true;
        }

        consecutiveErrors++;
        if (consecutiveErrors == 1 || consecutiveErrors % ErrorLogEvery == 0)
        {
            _logger.LogError(ex, "{Loop}: receive error (consecutive={Count})", loop, consecutiveErrors);
        }

        if (consecutiveErrors >= MaxConsecutiveErrors)
        {
            _logger.LogError("{Loop}: {Count} consecutive errors, giving up", loop, consecutiveErrors);
            return true;
        }

        // 指数バックオフ（上限 1 秒）。キャンセルされたら即抜ける
        int delayMs = Math.Min(ErrorBackoffMaxMs, ErrorBackoffBaseMs << Math.Min(consecutiveErrors, 10));
        return _cts.Token.WaitHandle.WaitOne(delayMs);
    }

    private void FlowLoop()
    {
        int consecutiveErrors = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!_flowSource.TryReceive(out var ev))
                {
                    break;
                }

                consecutiveErrors = 0;
                Interlocked.Increment(ref _flowEvents);
                if (_options.LogFlowEvents)
                {
                    _logger.LogInformation(
                        "FLOW {Event} pid={Pid} {Key} outbound={Outbound} loopback={Loopback} rawLocal=[{W0:x8} {W1:x8} {W2:x8} {W3:x8}] rawPort={RawPort} endpoint={EndpointId}",
                        ev.Established ? "ESTABLISHED" : "DELETED", ev.Pid, ev.Key, ev.Outbound, ev.Loopback,
                        ev.RawLocalWords.W0, ev.RawLocalWords.W1, ev.RawLocalWords.W2, ev.RawLocalWords.W3, ev.RawLocalPort, ev.EndpointId);
                }

                if (ev.Established)
                {
                    FlowTable.OnFlowEstablished(ev.Key, ev.Pid, ev.EndpointId);
                }
                else
                {
                    FlowTable.OnFlowDeleted(ev.Key);
                }
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                if (HandleLoopError(ex, "Flow receive loop", ref consecutiveErrors))
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Flow receive loop exited");
    }

    private void PacketLoop()
    {
        var buffer = new byte[ReceiveBufferSize];
        var metas = new PacketMeta[WinDivertConstants.BatchMax];
        int consecutiveErrors = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!_packetSource.TryReceiveBatch(buffer, metas, out int bytes, out int count))
                {
                    break;
                }

                consecutiveErrors = 0;
                if (count == 0)
                {
                    continue;
                }

                Interlocked.Increment(ref _batchesReceived);
                Interlocked.Add(ref _packetsReceived, count);
                ProcessBatch(buffer.AsSpan(0, bytes), metas.AsSpan(0, count));
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                if (HandleLoopError(ex, "Packet receive loop", ref consecutiveErrors))
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Packet receive loop exited");
    }

    /// <summary>連結されたバッチを IP ヘッダの長さで分割して分類する。境界が壊れたらそのバッチの残りは捨てる（テスト用に internal）。</summary>
    internal void ProcessBatch(ReadOnlySpan<byte> data, ReadOnlySpan<PacketMeta> metas)
    {
        int offset = 0;
        for (int i = 0; i < metas.Length; i++)
        {
            if (offset >= data.Length || !PacketParser.TryParse(data[offset..], out var packet))
            {
                Aggregator.RecordParseFailure();
                return; // 境界が分からなくなったのでこのバッチの残りは諦める
            }

            PacketClassifier.Classify(packet, metas[i], FlowTable, Aggregator);
            offset += packet.TotalLength;
        }
    }

    private void SnapshotLoop()
    {
        while (!_cts.Token.WaitHandle.WaitOne(_options.SnapshotInterval))
        {
            ApplySnapshot();
        }
    }

    /// <summary>受信を止め、スレッドの終了を待ってからハンドルを閉じる。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            _flowSource.Shutdown();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Flow source shutdown failed");
        }

        try
        {
            _packetSource.Shutdown();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Packet source shutdown failed");
        }

        JoinQuietly(_flowThread);
        JoinQuietly(_packetThread);
        JoinQuietly(_snapshotThread);
        _flowSource.Dispose();
        _packetSource.Dispose();
        _cts.Dispose();
        _logger.LogInformation("MonitorEngine disposed");
    }

    private void JoinQuietly(Thread thread)
    {
        if (thread.IsAlive && !thread.Join(TimeSpan.FromSeconds(2)))
        {
            // Shutdown で受信のブロックが解けていない可能性。ハンドルは続けて閉じる（フェイルセーフ優先）
            _logger.LogWarning("Thread {Name} did not exit within the join timeout", thread.Name);
        }
    }
}
