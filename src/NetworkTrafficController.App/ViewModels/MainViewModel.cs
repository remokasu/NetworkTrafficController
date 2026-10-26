using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NetworkTrafficController.App.Services;
using NetworkTrafficController.Core.Control;
using NetworkTrafficController.Core.Exceptions;
using NetworkTrafficController.Core.Mapping;
using NetworkTrafficController.Core.Monitoring;
using NetworkTrafficController.Core.Native;

namespace NetworkTrafficController.App.ViewModels;

/// <summary>メイン画面のビューモデル。500ms〜1s の <see cref="DispatcherTimer"/> で集計を取り込み、制御を操作する。</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(1000);
    private const int GraphPoints = 120;

    private readonly MonitorHost _host;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<uint, ProcessRowViewModel> _rowByPid = new();
    private readonly Dictionary<(byte, NetAddress, ushort, NetAddress, ushort), ConnectionRowViewModel> _connByKey = new();
    private readonly SpeedHistory _totalHistory = new(GraphPoints);
    // プロセスごとの直近の履歴。選び直したときに、その組み合わせの過去の合計をすぐ描けるように全プロセス分持つ
    private readonly Dictionary<uint, SpeedHistory> _historyByPid = new();
    private IReadOnlyList<ProcessRowViewModel> _selection = [];
    private MonitorSnapshot? _lastSnapshot;
    private double _lastTotalOut;
    private double _lastTotalIn;

    public ObservableCollection<ProcessRowViewModel> Processes { get; } = new();
    public ObservableCollection<ConnectionRowViewModel> Connections { get; } = new();

    // 一覧（DataGrid）が見ている既定のビュー。絞り込みはここに Filter を付け、行の追加・削除のロジックには手を入れない
    private readonly ICollectionView _processView;

    /// <summary>一覧の絞り込み文字列（名前・PID の部分一致、<see cref="ProcessFilter"/>）。空なら全件。</summary>
    [ObservableProperty] private string _filterText = string.Empty;

    [ObservableProperty] private string _statusText = "starting...";
    [ObservableProperty] private string _totalRateText = string.Empty;
    [ObservableProperty] private string _controlSummary = "No control";
    [ObservableProperty] private string _graphTitle = "All processes";
    [ObservableProperty] private string? _graphToolTip;
    // グラフの凡例を兼ねる。送信（↑）は線と同じ水色、受信（↓）は緑で表示する
    [ObservableProperty] private string _graphOutText = string.Empty;
    [ObservableProperty] private string _graphInText = string.Empty;
    [ObservableProperty] private Geometry _outGeometry = Geometry.Empty;
    [ObservableProperty] private Geometry _inGeometry = Geometry.Empty;

    /// <summary>右側の制御パネル。</summary>
    public ControlPanelViewModel Panel { get; }

    public MainViewModel(MonitorHost host, ILoggerFactory loggerFactory)
    {
        _host = host;
        _logger = loggerFactory.CreateLogger<MainViewModel>();
        Panel = new ControlPanelViewModel(host, loggerFactory);
        _processView = CollectionViewSource.GetDefaultView(Processes);
        _processView.Filter = o => o is ProcessRowViewModel row && ProcessFilter.Matches(FilterText, row.Name, row.Pid);
        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => Tick();
    }

    // 文字を打つたびに絞り直す。隠れた行の選択は DataGrid が Reset 通知を受けて外す
    partial void OnFilterTextChanged(string value) => _processView.Refresh();

    /// <summary>絞り込みを消して全件に戻す（検索欄の ✕）。</summary>
    [RelayCommand]
    private void ClearFilter() => FilterText = string.Empty;

    /// <summary>監視を開始してタイマを回す。ドライバ読み込み失敗時はステータスに理由を出し、監視・制御を無効化する。</summary>
    public void Start()
    {
        TryStartMonitoring(1);
    }

    private static readonly DriverStartRetryPolicy RetryPolicy = DriverStartRetryPolicy.Default;
    private DispatcherTimer? _retryTimer;
    private bool _disposed;

    // ドライバが停止処理中などの一時的な失敗なら、画面を止めずにタイマで待ってからやり直す（DriverStartRetryPolicy）
    private void TryStartMonitoring(int attempt)
    {
        // 待っている間にウィンドウを閉じた場合は、閉じた後にドライバを開かない
        if (_disposed)
        {
            return;
        }

        try
        {
            _host.StartMonitoring();
            StatusText = $"Monitoring  WinDivert {_host.DriverVersion.Major}.{_host.DriverVersion.Minor}";
            _timer.Start();
            if (attempt > 1)
            {
                _logger.LogInformation("monitoring started after {Attempt} attempts", attempt);
            }
        }
        catch (WinDivertException ex) when (RetryPolicy.ShouldRetry(ex, attempt))
        {
            StatusText = $"Waiting for the WinDivert driver to become ready ({attempt}/{RetryPolicy.MaxAttempts}): {WinDivertException.Describe(ex.Failure)}";
            _logger.LogWarning(ex, "monitoring start failed transiently (attempt {Attempt}); retrying in {Interval}", attempt, RetryPolicy.Interval);
            _retryTimer?.Stop();
            _retryTimer = new DispatcherTimer { Interval = RetryPolicy.Interval };
            _retryTimer.Tick += (_, _) =>
            {
                _retryTimer?.Stop();
                _retryTimer = null;
                TryStartMonitoring(attempt + 1);
            };
            _retryTimer.Start();
        }
        catch (WinDivertException ex)
        {
            StatusText = $"Failed to start WinDivert: {WinDivertException.Describe(ex.Failure)} (Win32 {ex.Win32Error})";
            _logger.LogError(ex, "monitoring failed to start (attempt {Attempt})", attempt);
        }
    }

    private int _tickCount;

    private void Tick()
    {
        // 数十秒に一度、終了したプロセスの制御対象を掃除する（CatchAll ハンドルを開きっぱなしにしない）
        if (++_tickCount % 15 == 0 && _host.Store.AnyConfigured)
        {
            _host.PruneDeadPidTargets();
        }

        var snapshot = _host.Aggregator.Snapshot();
        var (totalOut, totalIn, rates) = _host.Rates.Update(snapshot);

        _totalHistory.Add(totalOut, totalIn);
        _lastSnapshot = snapshot;
        UpdateProcessRows(rates);
        UpdateProcessHistories();
        UpdateConnections(snapshot);
        UpdateControlSummary();

        TotalRateText = $"Total ↑{Formatting.Rate(totalOut)}  ↓{Formatting.Rate(totalIn)}";
        if (_host.Monitor is { } m)
        {
            StatusText = $"Monitoring  WinDivert {_host.DriverVersion.Major}.{_host.DriverVersion.Minor}  packets={m.PacketCount}  flows={_host.FlowTable.ExactCount}  control={(_host.ControlActive ? "ON (capturing all TCP/UDP)" : "OFF")}";
        }

        UpdateGraph(totalOut, totalIn);
    }

    // 行の更新中か。選択中の行を消すと DataGrid が同期的に SelectionChanged を出し、SetSelection が Tick の途中に割り込むため
    private bool _updatingRows;

    private void UpdateProcessRows(IReadOnlyList<ProcessRate> rates)
    {
        _updatingRows = true;
        try
        {
            UpdateProcessRowsCore(rates);
        }
        finally
        {
            _updatingRows = false;
        }
    }

    private void UpdateProcessRowsCore(IReadOnlyList<ProcessRate> rates)
    {
        var seen = new HashSet<uint>();
        foreach (var r in rates)
        {
            seen.Add(r.Pid);
            if (!_rowByPid.TryGetValue(r.Pid, out var row))
            {
                row = new ProcessRowViewModel(r.Pid) { Name = _host.Resolver.Resolve(r.Pid).Name };
                _rowByPid[r.Pid] = row;
                Processes.Add(row);
            }

            row.OutRate = r.OutBytesPerSecond;
            row.InRate = r.InBytesPerSecond;
            row.OutTotal = r.Total.OutBytes;
            row.InTotal = r.Total.InBytes;
            row.ConnectionCount = r.ConnectionCount;
            row.Control = DescribeRowControl(r.Pid);
        }

        for (int i = Processes.Count - 1; i >= 0; i--)
        {
            var row = Processes[i];
            if (seen.Contains(row.Pid))
            {
                continue;
            }

            // 制御中のプロセスは、通信量が 0 でも一覧から消さない。監視は制御を通過した後のパケットを数えるので、
            // Block した対象は監視上 0 になり、そのまま消すと選び直して解除できなくなる
            if (_host.Store.TryGetConfiguredPolicy(row.Pid, out _))
            {
                row.OutRate = 0;
                row.InRate = 0;
                row.Control = DescribeRowControl(row.Pid);
                continue;
            }

            // 集計から消えた（通信が途絶えた・終了した）制御していないプロセスの行を除く
            _rowByPid.Remove(row.Pid);
            Processes.RemoveAt(i);
        }
    }

    /// <summary>「制御」列の表示。制御中なら設定内容と、その対象で破棄したパケット数を出す。</summary>
    private string DescribeRowControl(uint pid)
    {
        if (!_host.Store.TryGetConfiguredPolicy(pid, out var policy))
        {
            return string.Empty;
        }

        string text = ProcessRowViewModel.DescribeControl(policy, _host.Store.BlockOverride);
        if (_host.Store.TryGetExistingController(pid, out var controller))
        {
            long dropped = controller.DroppedPackets;
            long delayed = controller.DelayedPackets;
            if (dropped > 0)
            {
                text += string.Create(CultureInfo.InvariantCulture, $"  [dropped {dropped:N0}]");
            }

            if (delayed > 0)
            {
                text += string.Create(CultureInfo.InvariantCulture, $"  [delayed {delayed:N0}]");
            }
        }

        return text;
    }

    // 一覧に出ている全プロセスの履歴に 1 点ずつ足し、一覧から消えたプロセスの履歴は捨てる
    private void UpdateProcessHistories()
    {
        foreach (var row in _rowByPid.Values)
        {
            if (!_historyByPid.TryGetValue(row.Pid, out var history))
            {
                history = new SpeedHistory(GraphPoints);
                _historyByPid[row.Pid] = history;
            }

            history.Add(row.OutRate, row.InRate);
        }

        foreach (uint pid in _historyByPid.Keys.Where(p => !_rowByPid.ContainsKey(p)).ToList())
        {
            _historyByPid.Remove(pid);
        }
    }

    private void UpdateConnections(MonitorSnapshot snapshot)
    {
        if (_selection.Count == 0)
        {
            if (Connections.Count > 0)
            {
                Connections.Clear();
                _connByKey.Clear();
            }

            return;
        }

        var pids = _selection.Select(r => r.Pid).ToHashSet();
        var seen = new HashSet<(byte, NetAddress, ushort, NetAddress, ushort)>();
        foreach (var c in snapshot.Connections)
        {
            if (!pids.Contains(c.Pid))
            {
                continue;
            }

            var key = (c.Key.Protocol, c.Key.LocalAddress, c.Key.LocalPort, c.Key.RemoteAddress, c.Key.RemotePort);
            seen.Add(key);
            if (!_connByKey.TryGetValue(key, out var row))
            {
                string name = _rowByPid.TryGetValue(c.Pid, out var owner) ? owner.Name : _host.Resolver.Resolve(c.Pid).Name;
                row = new ConnectionRowViewModel(c, name);
                _connByKey[key] = row;
                Connections.Add(row);
            }

            row.OutBytes = c.Counters.OutBytes;
            row.InBytes = c.Counters.InBytes;
        }

        for (int i = Connections.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Connections[i].Key))
            {
                _connByKey.Remove(Connections[i].Key);
                Connections.RemoveAt(i);
            }
        }
    }

    private void UpdateControlSummary()
    {
        long targets = _host.Store.AnyConfigured ? _rowByPid.Keys.Count(p => _host.Store.TryGetConfiguredPolicy(p, out _)) : 0;
        var s = _host.ControlStatistics;
        if (!_host.ControlActive)
        {
            ControlSummary = "No control";
            return;
        }

        string block = _host.Store.BlockOverride ? "  [Block override ON]" : string.Empty;
        ControlSummary = string.Create(CultureInfo.InvariantCulture,
            $"Control ON  targets={targets}  dropped(block={s.DroppedBlock} stop={s.DroppedStopped} drop%={s.DroppedRandom} overflow={s.DroppedQueueOverflow})  reset={s.ResetInjected}  delayed={s.DelayedPackets}p{block}");
    }

    private void UpdateGraph(double totalOut, double totalIn)
    {
        _lastTotalOut = totalOut;
        _lastTotalIn = totalIn;
        RefreshGraph();
    }

    // 何も選んでいなければ全体の合計、選んでいれば選んだプロセスの合計を描く
    private void RefreshGraph()
    {
        var selected = _selection.Where(r => _rowByPid.ContainsKey(r.Pid)).ToList();
        var outValues = new double[GraphPoints];
        var inValues = new double[GraphPoints];
        int n;
        if (selected.Count == 0)
        {
            n = SpeedHistory.SumLatest([_totalHistory], outValues, inValues);
            GraphTitle = "All processes";
            GraphToolTip = null;
            GraphOutText = $"↑{Formatting.Rate(_lastTotalOut)}";
            GraphInText = $"↓{Formatting.Rate(_lastTotalIn)}";
        }
        else
        {
            var histories = selected.Select(r => _historyByPid.TryGetValue(r.Pid, out var h) ? h : null).OfType<SpeedHistory>();
            n = SpeedHistory.SumLatest(histories, outValues, inValues);
            var names = selected.Select(r => (r.Name, r.Pid)).ToList();
            GraphTitle = ProcessSelectionText.Short(names);
            GraphToolTip = selected.Count > 1 ? ProcessSelectionText.Full(names) : null;
            GraphOutText = $"↑{Formatting.Rate(selected.Sum(r => r.OutRate))}";
            GraphInText = $"↓{Formatting.Rate(selected.Sum(r => r.InRate))}";
        }

        // 上りと下りを同じ尺度で描く（どちらか大きい方を上端にする）
        double max = 1;
        for (int i = 0; i < n; i++)
        {
            max = Math.Max(max, Math.Max(outValues[i], inValues[i]));
        }

        OutGeometry = BuildGeometry(outValues.AsSpan(0, n), max);
        InGeometry = BuildGeometry(inValues.AsSpan(0, n), max);
    }

    /// <summary>
    /// 1 本の線を作る。描画側は Stretch=Fill で枠いっぱいに伸ばすので、線を描かない図形で常に「横 0〜GraphPoints-1、縦 0〜max」の
    /// 範囲を含めておく。こうすると上りと下りの尺度がそろい、点が少ないときも右端（最新）に寄せて描ける。
    /// </summary>
    private static Geometry BuildGeometry(ReadOnlySpan<double> values, double max)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new System.Windows.Point(0, 0), isFilled: false, isClosed: false);
            ctx.LineTo(new System.Windows.Point(GraphPoints - 1, max), isStroked: false, isSmoothJoin: false);
            if (values.Length >= 2)
            {
                int offset = GraphPoints - values.Length;
                ctx.BeginFigure(new System.Windows.Point(offset, max - values[0]), isFilled: false, isClosed: false);
                for (int i = 1; i < values.Length; i++)
                {
                    ctx.LineTo(new System.Windows.Point(offset + i, max - values[i]), isStroked: true, isSmoothJoin: false);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>プロセス一覧の選択が変わったときに呼ぶ（複数選択。空なら全体）。</summary>
    public void SetSelection(IReadOnlyList<ProcessRowViewModel> rows)
    {
        _selection = rows;
        Connections.Clear();
        _connByKey.Clear();
        Panel.SetTargets(rows);
        if (_updatingRows)
        {
            // 接続一覧とグラフは、このあと Tick の続きで更新される
            return;
        }

        if (_lastSnapshot is { } snapshot)
        {
            UpdateConnections(snapshot);
        }

        RefreshGraph();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _retryTimer?.Stop();
        _retryTimer = null;
        _timer.Stop();
        Panel.Dispose();
    }
}
