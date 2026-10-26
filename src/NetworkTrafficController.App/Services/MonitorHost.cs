using Microsoft.Extensions.Logging;
using NetworkTrafficController.Core.Control;
using NetworkTrafficController.Core.Exceptions;
using NetworkTrafficController.Core.Mapping;
using NetworkTrafficController.Core.Monitoring;
using NetworkTrafficController.Core.Native;

namespace NetworkTrafficController.App.Services;

/// <summary>
/// 監視エンジンと制御エンジンを保持し、GUI に集計・名前解決・制御 API を提供する。
/// 制御ハンドルは、対象が 1 つ以上設定されたら開き、無くなったら閉じる（ADR-0002 §2）。
/// GUI では対象を実行中に動的に選ぶため、制御フィルタは <see cref="ControlFilterBuilder.CatchAll"/>（全 TCP/UDP）を使う。
/// 対象外プロセスは fail-open でそのまま通すので影響しないが、全パケットがユーザーモードを経由する点は UI で注意喚起する。
/// </summary>
public sealed class MonitorHost : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly object _controlLock = new();
    private MonitorEngine? _monitor;
    private ControlEngine? _control;
    private int _disposed;

    /// <summary>ロガーファクトリを指定して生成する。</summary>
    public MonitorHost(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MonitorHost>();
        Time = TimeProvider.System;
        Resolver = new ProcessNameResolver(loggerFactory.CreateLogger<ProcessNameResolver>(), Time);
        FlowTable = new FlowTable(new FlowTableOptions(), Time, loggerFactory.CreateLogger<FlowTable>());
        Aggregator = new MonitorAggregator(new MonitorAggregatorOptions(), Time);
        Store = new ControlPolicyStore(Resolver, Time);
        ControlStatistics = new ControlStatistics();
        Rates = new RateCalculator(Time);
    }

    /// <summary>時間源。</summary>
    public TimeProvider Time { get; }

    /// <summary>PID → プロセス名の解決。</summary>
    public ProcessNameResolver Resolver { get; }

    /// <summary>5-tuple→PID 照合表（監視・制御で共有）。</summary>
    public FlowTable FlowTable { get; }

    /// <summary>監視の集計。</summary>
    public MonitorAggregator Aggregator { get; }

    /// <summary>制御ポリシー。</summary>
    public ControlPolicyStore Store { get; }

    /// <summary>制御統計（制御エンジンを開き直しても引き継ぐ）。</summary>
    public ControlStatistics ControlStatistics { get; }

    /// <summary>速度計算（前回スナップショットとの差分）。</summary>
    public RateCalculator Rates { get; }

    /// <summary>ドライバのバージョン（監視開始後に有効）。</summary>
    public (ulong Major, ulong Minor) DriverVersion { get; private set; }

    /// <summary>監視が起動できたか（ドライバ読み込み成功）。false なら制御ハンドルも開かない。</summary>
    public bool MonitoringAvailable { get; private set; }

    /// <summary>制御ハンドルが開いているか。</summary>
    public bool ControlActive
    {
        get { lock (_controlLock) { return _control is not null; } }
    }

    /// <summary>監視を開始する。失敗時は <see cref="WinDivertException"/> を投げる。</summary>
    public void StartMonitoring()
    {
        // 起動のやり直し（DriverStartRetryPolicy）が入ったので、閉じた後や二重の呼び出しでハンドルを開かないよう拒む
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_monitor is not null)
        {
            throw new InvalidOperationException("Monitoring has already started");
        }

        var (flowSource, sniffSource) = MonitorSources.Open(_loggerFactory);
        DriverVersion = sniffSource.DriverVersion;
        _monitor = new MonitorEngine(
            flowSource,
            sniffSource,
            new WindowsConnectionSnapshotProvider(),
            FlowTable,
            Aggregator,
            new MonitorEngineOptions(),
            Time,
            _loggerFactory.CreateLogger<MonitorEngine>());
        _monitor.Start();
        MonitoringAvailable = true;
        _logger.LogInformation("MonitorHost monitoring started (driver {Major}.{Minor})", DriverVersion.Major, DriverVersion.Minor);
    }

    /// <summary>監視エンジンのカウンタ（受信パケット数など）。監視前は null。</summary>
    public MonitorEngine? Monitor => _monitor;

    /// <summary>
    /// 制御対象の設定が変わったときに呼ぶ。対象が有れば制御ハンドルを開き、無ければ閉じる。
    /// 監視が起動していない（ドライバ読み込み失敗）ときや、制御ハンドルを開けなかったときは例外を投げず、
    /// 制御なしの状態のままにする（SPEC: ドライバ失敗時は監視・制御を無効化して起動を続ける）。
    /// </summary>
    public void SyncControlEngine()
    {
        lock (_controlLock)
        {
            if (_disposed != 0)
            {
                return;
            }

            bool shouldRun = MonitoringAvailable && Store.AnyConfigured;
            if (shouldRun && _control is null)
            {
                try
                {
                    var control = ControlEngine.CreateWithWinDivert(ControlFilterBuilder.CatchAll, FlowTable, Store, ControlStatistics, Time, _loggerFactory.CreateLogger<ControlEngine>());
                    control.Stopped += OnControlStopped;
                    control.Start();
                    _control = control;
                    _logger.LogInformation("Control engine started");
                }
                catch (WinDivertException ex)
                {
                    _logger.LogError(ex, "control engine failed to start; continuing without control");
                }
            }
            else if (!shouldRun && _control is not null)
            {
                _control.Dispose();
                _control = null;
                _logger.LogInformation("Control engine stopped (no targets)");
            }
        }
    }

    /// <summary>
    /// 制御の受信ループが致命停止したときに（エンジンのスレッドから）呼ばれる。ハンドルを閉じて通信を元に戻す。
    /// エンジンのスレッドから同期 Dispose するとデッドロックするので、別スレッドで閉じる。
    /// </summary>
    private void OnControlStopped()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_controlLock)
            {
                if (_control is not null)
                {
                    _logger.LogWarning("Control engine stopped abnormally; closing control handle (fail-open)");
                    _control.Dispose();
                    _control = null;
                }
            }
        });
    }

    /// <summary>
    /// 設定済みの PID 対象のうち、プロセスが終了しているものを掃除する（CatchAll ハンドルを開きっぱなしにしない）。
    /// 掃除した結果 UI に反映が必要なら true を返す。
    /// </summary>
    public bool PruneDeadPidTargets()
    {
        bool removed = false;
        foreach (uint pid in Store.ConfiguredPids)
        {
            Resolver.Invalidate(pid); // キャッシュを捨てて実在を確かめる
            if (!Resolver.Resolve(pid).Resolved)
            {
                Store.RemovePid(pid);
                removed = true;
            }
        }

        if (removed)
        {
            SyncControlEngine();
        }

        return removed;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // 制御を先に閉じる（遅延キューを再注入してからハンドル解放）。監視はその後
        lock (_controlLock)
        {
            _control?.Dispose();
            _control = null;
        }

        _monitor?.Dispose();
        HandleRegistry.CloseAll();
        _logger.LogInformation("MonitorHost disposed");
    }
}
