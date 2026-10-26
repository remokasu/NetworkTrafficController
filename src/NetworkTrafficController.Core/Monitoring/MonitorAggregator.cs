using NetworkTrafficController.Core.Mapping;

namespace NetworkTrafficController.Core.Monitoring;

/// <summary>送受信それぞれのバイト数・パケット数（不変のスナップショット用）。</summary>
public readonly record struct TrafficCounters(long OutBytes, long OutPackets, long InBytes, long InPackets)
{
    /// <summary>合計バイト数。</summary>
    public long TotalBytes => OutBytes + InBytes;

    /// <summary>差分を取る。</summary>
    public TrafficCounters Subtract(in TrafficCounters other) =>
        new(OutBytes - other.OutBytes, OutPackets - other.OutPackets, InBytes - other.InBytes, InPackets - other.InPackets);
}

/// <summary>プロセス別の集計（スナップショット）。</summary>
public sealed record ProcessTraffic(uint Pid, TrafficCounters Counters, int ConnectionCount, long LastSeen);

/// <summary>接続（プロセス × 5-tuple）別の集計（スナップショット）。</summary>
public sealed record ConnectionTraffic(uint Pid, FlowKey Key, bool Loopback, TrafficCounters Counters, long FirstSeen, long LastSeen);

/// <summary>集計スナップショット。GUI / CLI はこれを一定間隔で取得して差分から速度を出す。</summary>
public sealed record MonitorSnapshot(
    long Timestamp,
    TrafficCounters Total,
    TrafficCounters UnclassifiedTcp,
    TrafficCounters UnclassifiedUdp,
    long OtherProtocolPackets,
    long OtherProtocolBytes,
    long ParseFailures,
    long LoopbackPackets,
    IReadOnlyList<ProcessTraffic> Processes,
    IReadOnlyList<ConnectionTraffic> Connections);

/// <summary><see cref="MonitorAggregator"/> の上限設定（ADR-0005 §7）。</summary>
public sealed class MonitorAggregatorOptions
{
    /// <summary>同時に保持するプロセス数の上限。</summary>
    public int MaxProcesses { get; init; } = 1_024;

    /// <summary>同時に保持する接続数の上限。</summary>
    public int MaxConnections { get; init; } = 16_384;
}

/// <summary>
/// 監視結果の集計。書き込みは監視受信スレッドのみ、読み取りは <see cref="Snapshot"/>（ロックで保護）。
/// loopback パケットは送信側・受信側の 2 プロセスに帰属しうるが、全体合計は 1 回だけ数える（ADR-0003）。
/// </summary>
public sealed class MonitorAggregator
{
    private sealed class MutableCounters
    {
        public long OutBytes;
        public long OutPackets;
        public long InBytes;
        public long InPackets;

        public void Add(Direction direction, int bytes)
        {
            if (direction == Direction.Outbound)
            {
                OutBytes += bytes;
                OutPackets++;
            }
            else
            {
                InBytes += bytes;
                InPackets++;
            }
        }

        public TrafficCounters ToCounters() => new(OutBytes, OutPackets, InBytes, InPackets);
    }

    private sealed class ProcessState
    {
        public MutableCounters Counters { get; } = new();
        public HashSet<FlowKey> Connections { get; } = new();
        public long LastSeen { get; set; }
    }

    private sealed class ConnectionState
    {
        public required uint Pid { get; init; }
        public required FlowKey Key { get; init; }
        public required bool Loopback { get; init; }
        public MutableCounters Counters { get; } = new();
        public long FirstSeen { get; init; }
        public long LastSeen { get; set; }
    }

    private readonly object _lock = new();
    private readonly MonitorAggregatorOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<uint, ProcessState> _processes = new();
    private readonly Dictionary<(uint Pid, FlowKey Key), ConnectionState> _connections = new();
    private readonly MutableCounters _total = new();
    private readonly MutableCounters _unclassifiedTcp = new();
    private readonly MutableCounters _unclassifiedUdp = new();
    private long _otherProtocolPackets;
    private long _otherProtocolBytes;
    private long _parseFailures;
    private long _loopbackPackets;

    /// <summary>設定と時間源を指定して生成する。</summary>
    public MonitorAggregator(MonitorAggregatorOptions options, TimeProvider time)
    {
        _options = options;
        _time = time;
    }

    /// <summary>1 パケットを全体合計に計上する（帰属先に関係なく 1 回）。</summary>
    public void RecordPacket(Direction direction, int bytes, bool loopback)
    {
        lock (_lock)
        {
            _total.Add(direction, bytes);
            if (loopback)
            {
                _loopbackPackets++;
            }
        }
    }

    /// <summary>プロセスと接続に帰属させる。loopback では送信側・受信側で 2 回呼ばれる。</summary>
    public void Attribute(uint pid, in FlowKey key, Direction direction, int bytes, bool loopback)
    {
        long now = _time.GetTimestamp();
        lock (_lock)
        {
            if (!_processes.TryGetValue(pid, out var process))
            {
                EvictProcessesIfNeeded();
                process = new ProcessState();
                _processes[pid] = process;
            }

            process.Counters.Add(direction, bytes);
            process.LastSeen = now;
            process.Connections.Add(key);

            var connectionKey = (pid, key);
            if (!_connections.TryGetValue(connectionKey, out var connection))
            {
                EvictConnectionsIfNeeded();
                connection = new ConnectionState { Pid = pid, Key = key, Loopback = loopback, FirstSeen = now };
                _connections[connectionKey] = connection;
            }

            connection.Counters.Add(direction, bytes);
            connection.LastSeen = now;
        }
    }

    /// <summary>照合できなかった TCP / UDP パケットを計上する（観測可能性）。</summary>
    public void RecordUnclassified(byte protocol, Direction direction, int bytes)
    {
        lock (_lock)
        {
            (protocol == IpProtocol.Tcp ? _unclassifiedTcp : _unclassifiedUdp).Add(direction, bytes);
        }
    }

    /// <summary>TCP / UDP 以外のパケットを計上する。</summary>
    public void RecordOtherProtocol(int bytes)
    {
        lock (_lock)
        {
            _otherProtocolPackets++;
            _otherProtocolBytes += bytes;
        }
    }

    /// <summary>解析に失敗したパケットを計上する。</summary>
    public void RecordParseFailure()
    {
        lock (_lock)
        {
            _parseFailures++;
        }
    }

    /// <summary>現在の集計を不変のスナップショットとして取り出す。</summary>
    public MonitorSnapshot Snapshot()
    {
        lock (_lock)
        {
            var processes = new List<ProcessTraffic>(_processes.Count);
            foreach (var (pid, state) in _processes)
            {
                processes.Add(new ProcessTraffic(pid, state.Counters.ToCounters(), state.Connections.Count, state.LastSeen));
            }

            var connections = new List<ConnectionTraffic>(_connections.Count);
            foreach (var state in _connections.Values)
            {
                connections.Add(new ConnectionTraffic(state.Pid, state.Key, state.Loopback, state.Counters.ToCounters(), state.FirstSeen, state.LastSeen));
            }

            return new MonitorSnapshot(
                _time.GetTimestamp(),
                _total.ToCounters(),
                _unclassifiedTcp.ToCounters(),
                _unclassifiedUdp.ToCounters(),
                _otherProtocolPackets,
                _otherProtocolBytes,
                _parseFailures,
                _loopbackPackets,
                processes,
                connections);
        }
    }

    /// <summary>指定時間以上更新のない接続とプロセスを削除する。</summary>
    public int Expire(TimeSpan connectionIdle, TimeSpan processIdle)
    {
        long now = _time.GetTimestamp();
        int removed = 0;
        lock (_lock)
        {
            foreach (var (key, state) in _connections.ToList())
            {
                if (_time.GetElapsedTime(state.LastSeen, now) >= connectionIdle)
                {
                    _connections.Remove(key);
                    if (_processes.TryGetValue(state.Pid, out var process))
                    {
                        process.Connections.Remove(state.Key);
                    }

                    removed++;
                }
            }

            foreach (var (pid, state) in _processes.ToList())
            {
                if (state.Connections.Count == 0 && _time.GetElapsedTime(state.LastSeen, now) >= processIdle)
                {
                    _processes.Remove(pid);
                    removed++;
                }
            }
        }

        return removed;
    }

    // 上限到達後にパケットごとに全件ソートしないよう、上限の 10%（最低 1 件）をまとめて追い出す
    private const int EvictionBatchDivisor = 10;

    private void EvictProcessesIfNeeded()
    {
        if (_processes.Count < _options.MaxProcesses)
        {
            return;
        }

        int count = Math.Max(1, _options.MaxProcesses / EvictionBatchDivisor);
        foreach (var pid in _processes.OrderBy(kv => kv.Value.LastSeen).Take(count).Select(kv => kv.Key).ToList())
        {
            _processes.Remove(pid);
        }
    }

    private void EvictConnectionsIfNeeded()
    {
        if (_connections.Count < _options.MaxConnections)
        {
            return;
        }

        int count = Math.Max(1, _options.MaxConnections / EvictionBatchDivisor);
        foreach (var victim in _connections.OrderBy(kv => kv.Value.LastSeen).Take(count).ToList())
        {
            _connections.Remove(victim.Key);
            if (_processes.TryGetValue(victim.Value.Pid, out var process))
            {
                process.Connections.Remove(victim.Value.Key);
            }
        }
    }
}
