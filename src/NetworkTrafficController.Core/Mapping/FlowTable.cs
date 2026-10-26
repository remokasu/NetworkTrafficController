using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NetworkTrafficController.Core.Mapping;

/// <summary>照合表エントリの供給元。</summary>
public enum FlowSource
{
    /// <summary>WinDivert FLOW レイヤーのイベント。</summary>
    FlowEvent,

    /// <summary>GetExtendedTcpTable / GetExtendedUdpTable のスナップショット。</summary>
    Snapshot,
}

/// <summary><see cref="FlowTable.TryResolve"/> がどの表で照合できたか。</summary>
public enum ResolveSource
{
    /// <summary>照合できなかった（fail-open）。</summary>
    None,

    /// <summary>Exact 表（5-tuple 完全一致）。</summary>
    Exact,

    /// <summary>Endpoint 表（ローカルアドレス + ポートの一致）。</summary>
    Endpoint,

    /// <summary>Endpoint 表のワイルドカード行（0.0.0.0 / :: で待受）。</summary>
    Wildcard,
}

/// <summary><see cref="FlowTable"/> の設定値。</summary>
public sealed class FlowTableOptions
{
    /// <summary>FLOW_DELETED / スナップショット消失の後、エントリを残す猶予（既定 10 秒）。</summary>
    public TimeSpan GracePeriod { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Exact 表の件数上限（既定 65,536）。</summary>
    public int MaxEntries { get; init; } = 65_536;
}

/// <summary>
/// 5-tuple→PID 照合表（SSOT）。Exact 表と Endpoint 表の 2 段構成。
/// 書き込みは FLOW 受信スレッドとスナップショット処理の 2 経路、読み取りは監視 / 制御スレッド（ADR-0001）。
/// </summary>
public sealed class FlowTable
{
    private sealed class FlowEntry
    {
        public required uint Pid { get; set; }
        public required FlowSource Source { get; set; }
        public ulong EndpointId { get; set; }
        public long CreatedAt { get; init; }
        public long? DeletedAt { get; set; }
    }

    private sealed class EndpointTables
    {
        public Dictionary<EndpointKey, uint> Exact { get; } = new();
        public Dictionary<(byte Protocol, ushort Port), uint> Wildcard { get; } = new();
    }

    private const int EvictionBatchDivisor = 10;
    private readonly ConcurrentDictionary<FlowKey, FlowEntry> _exact = new();
    private readonly FlowTableOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    /// <summary>差し替え方式の Endpoint 表。他スレッドへの可視性のため volatile。</summary>
    private volatile EndpointTables _endpoints = new();
    private long _conflicts;

    /// <summary>設定・時間源・ロガーを指定して生成する。</summary>
    public FlowTable(FlowTableOptions options, TimeProvider time, ILogger logger)
    {
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Exact 表の件数（削除猶予中を含む）。</summary>
    public int ExactCount => _exact.Count;

    /// <summary>Endpoint 表の件数（ワイルドカード行を含む）。</summary>
    public int EndpointCount
    {
        get
        {
            var e = _endpoints;
            return e.Exact.Count + e.Wildcard.Count;
        }
    }

    /// <summary>FLOW イベントとスナップショットで PID が食い違った回数（観測可能性）。</summary>
    public long ConflictCount => Interlocked.Read(ref _conflicts);

    /// <summary>
    /// 指定 PID 群のローカルポートを集める（制御ハンドルのフィルタ生成用、ADR-0002 B-2）。
    /// 削除猶予中のエントリも含める。<paramref name="max"/> を超えたら打ち切って false を返す（呼び出し側は全通しにフォールバック）。
    /// </summary>
    public bool TryCollectLocalPorts(Func<uint, bool> isTarget, int max, out IReadOnlyList<ushort> tcpPorts, out IReadOnlyList<ushort> udpPorts)
    {
        var tcp = new HashSet<ushort>();
        var udp = new HashSet<ushort>();

        foreach (var (key, entry) in _exact)
        {
            if (isTarget(entry.Pid))
            {
                (key.Protocol == IpProtocol.Udp ? udp : tcp).Add(key.LocalPort);
            }
        }

        var endpoints = _endpoints;
        foreach (var (key, pid) in endpoints.Exact)
        {
            if (isTarget(pid))
            {
                (key.Protocol == IpProtocol.Udp ? udp : tcp).Add(key.Port);
            }
        }

        foreach (var ((protocol, port), pid) in endpoints.Wildcard)
        {
            if (isTarget(pid))
            {
                (protocol == IpProtocol.Udp ? udp : tcp).Add(port);
            }
        }

        tcpPorts = tcp.ToList();
        udpPorts = udp.ToList();
        return tcp.Count + udp.Count <= max;
    }

    /// <summary>FLOW_ESTABLISHED を反映する。既存のスナップショット由来エントリは上書きする（FLOW 優先）。</summary>
    public void OnFlowEstablished(in FlowKey key, uint pid, ulong endpointId)
    {
        long now = _time.GetTimestamp();
        _exact.AddOrUpdate(
            key,
            _ => new FlowEntry { Pid = pid, Source = FlowSource.FlowEvent, EndpointId = endpointId, CreatedAt = now },
            (k, existing) =>
            {
                if (existing.Pid != pid && existing.DeletedAt is null)
                {
                    Interlocked.Increment(ref _conflicts);
                    _logger.LogDebug("FlowTable conflict for {Key}: {OldPid} ({OldSource}) -> {NewPid} (FlowEvent)", k, existing.Pid, existing.Source, pid);
                }

                existing.Pid = pid;
                existing.Source = FlowSource.FlowEvent;
                existing.EndpointId = endpointId;
                existing.DeletedAt = null;
                return existing;
            });
        EnforceLimit();
    }

    /// <summary>FLOW_DELETED を反映する。即削除せず猶予期間の後に <see cref="Sweep"/> で消す。</summary>
    public void OnFlowDeleted(in FlowKey key)
    {
        if (_exact.TryGetValue(key, out var entry))
        {
            entry.DeletedAt ??= _time.GetTimestamp();
        }
    }

    /// <summary>
    /// iphlpapi のスナップショットを反映する。LISTEN 以外の TCP 行は Exact 表へ（FLOW 由来があれば触らない）、
    /// LISTEN 行と UDP 行は Endpoint 表へ（差し替え）。前回のスナップショット由来で今回消えた行は削除猶予に入れる。
    /// </summary>
    public void ApplySnapshot(IReadOnlyList<TcpConnectionRow> tcpRows, IReadOnlyList<UdpEndpointRow> udpRows)
    {
        long now = _time.GetTimestamp();
        var seen = new HashSet<FlowKey>();
        var endpoints = new EndpointTables();

        foreach (var row in tcpRows)
        {
            if (row.State == TcpState.Listen)
            {
                AddEndpoint(endpoints, row.Key.Local, row.Pid);
                continue;
            }

            seen.Add(row.Key);
            _exact.AddOrUpdate(
                row.Key,
                _ => new FlowEntry { Pid = row.Pid, Source = FlowSource.Snapshot, CreatedAt = now },
                (k, existing) =>
                {
                    if (existing.Source == FlowSource.FlowEvent)
                    {
                        if (existing.Pid != row.Pid && existing.DeletedAt is null)
                        {
                            Interlocked.Increment(ref _conflicts);
                            _logger.LogDebug("FlowTable conflict for {Key}: FlowEvent pid {OldPid} vs snapshot pid {NewPid}", k, existing.Pid, row.Pid);
                        }

                        return existing;
                    }

                    existing.Pid = row.Pid;
                    existing.DeletedAt = null;
                    return existing;
                });
        }

        foreach (var row in udpRows)
        {
            AddEndpoint(endpoints, row.Key, row.Pid);
        }

        foreach (var (key, entry) in _exact)
        {
            if (entry.Source == FlowSource.Snapshot && entry.DeletedAt is null && !seen.Contains(key))
            {
                entry.DeletedAt = now;
            }
        }

        _endpoints = endpoints;
        EnforceLimit();
    }

    /// <summary>
    /// パケットの 5-tuple から PID を引く。検索順: Exact → Endpoint（アドレス一致）→ Endpoint（ワイルドカード）→ 見つからない。
    /// </summary>
    public bool TryResolve(in FlowKey key, out uint pid, out ResolveSource source)
    {
        if (_exact.TryGetValue(key, out var entry))
        {
            pid = entry.Pid;
            source = ResolveSource.Exact;
            return true;
        }

        var endpoints = _endpoints;
        if (endpoints.Exact.TryGetValue(key.Local, out pid))
        {
            source = ResolveSource.Endpoint;
            return true;
        }

        if (endpoints.Wildcard.TryGetValue((key.Protocol, key.LocalPort), out pid))
        {
            source = ResolveSource.Wildcard;
            return true;
        }

        pid = 0;
        source = ResolveSource.None;
        return false;
    }

    /// <summary>猶予期間を過ぎた削除済みエントリを消す。定期的に呼ぶ。</summary>
    /// <returns>削除した件数。</returns>
    public int Sweep()
    {
        long now = _time.GetTimestamp();
        int removed = 0;
        foreach (var (key, entry) in _exact)
        {
            if (entry.DeletedAt is { } deletedAt && _time.GetElapsedTime(deletedAt, now) >= _options.GracePeriod)
            {
                if (_exact.TryRemove(key, out _))
                {
                    removed++;
                }
            }
        }

        return removed;
    }

    private static void AddEndpoint(EndpointTables tables, in EndpointKey key, uint pid)
    {
        if (key.Address.IsUnspecified)
        {
            tables.Wildcard[(key.Protocol, key.Port)] = pid;
        }
        else
        {
            tables.Exact[key] = pid;
        }
    }

    private void EnforceLimit()
    {
        if (_exact.Count <= _options.MaxEntries)
        {
            return;
        }

        // 1 件ずつ全件ソートすると上限到達後に O(n log n) がパケットごとに走るので、上限の 10% をまとめて追い出して償却する
        int target = Math.Max(_exact.Count - _options.MaxEntries, _options.MaxEntries / EvictionBatchDivisor);
        _logger.LogWarning("FlowTable exact entries {Count} exceed limit {Limit}; evicting {Target} oldest deleted/created entries", _exact.Count, _options.MaxEntries, target);
        var victims = _exact
            .OrderBy(kv => kv.Value.DeletedAt is null ? 1 : 0)
            .ThenBy(kv => kv.Value.CreatedAt)
            .Take(target)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in victims)
        {
            _exact.TryRemove(key, out _);
        }
    }
}
