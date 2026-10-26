using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NetworkTrafficController.App.Services;
using NetworkTrafficController.Core.Control;
using NetworkTrafficController.Core.Monitoring;

namespace NetworkTrafficController.App.ViewModels;

/// <summary>
/// 右側の制御パネル。各項目は「チェック = ON/OFF、隣の数値 = 設定値、その右 = 単位」で揃え、
/// チェックの変更も数値の確定（Enter またはフォーカス移動）も、選択中のプロセスに即時反映する（SPEC: GUI で即時切替）。
/// 複数のプロセスを選んだときは、全員で同じ項目はその値を、違う項目は中間の状態（チェック）や空欄（数値）で出す。
/// 触った項目だけを選んだ全員にそろえ、触っていない項目はそれぞれの設定を残す（<see cref="ControlPanelMix"/>）。
/// </summary>
public sealed partial class ControlPanelViewModel : ObservableObject, IDisposable
{
    // 帯域の単位の ComboBox の並び（KB/s / MB/s / Mbps）
    private static readonly BandwidthUnit[] IndexToUnit = [BandwidthUnit.KilobytesPerSecond, BandwidthUnit.MegabytesPerSecond, BandwidthUnit.MegabitsPerSecond];

    // ComboBox の未選択（混在）
    private const int Mixed = -1;

    // チェックを入れたときにすぐ使える既定値（未入力のまま ON にしても意味のある値になるように）
    private static readonly ControlPanelState Defaults = new()
    {
        DropPercent = "10",
        DelayMs = "100",
        JitterMs = "0",
        Upload = "1",
        UploadUnit = BandwidthUnit.MegabytesPerSecond,
        Download = "1",
        DownloadUnit = BandwidthUnit.MegabytesPerSecond,
    };

    private readonly MonitorHost _host;
    private readonly ILogger _logger;
    private readonly TimedBlockController _timedBlock;
    private IReadOnlyList<ProcessRowViewModel> _targets = [];
    private bool _loading;

    [ObservableProperty] private string _targetText = string.Empty;
    [ObservableProperty] private string? _targetToolTip;
    [ObservableProperty] private bool _hasTarget;

    // null = 選んだプロセスで設定が違う（混在）
    [ObservableProperty] private bool? _blockOn = false;
    [ObservableProperty] private bool? _resetOn = false;
    [ObservableProperty] private bool? _dropOn = false;
    [ObservableProperty] private string? _dropPercent = Defaults.DropPercent;
    [ObservableProperty] private bool? _delayOn = false;
    [ObservableProperty] private string? _delayMs = Defaults.DelayMs;
    [ObservableProperty] private string? _jitterMs = Defaults.JitterMs;
    [ObservableProperty] private bool? _uploadOn = false;
    [ObservableProperty] private string? _uploadValue = Defaults.Upload;
    [ObservableProperty] private int _uploadUnitIndex = UnitToIndex(Defaults.UploadUnit);
    [ObservableProperty] private bool? _downloadOn = false;
    [ObservableProperty] private string? _downloadValue = Defaults.Download;
    [ObservableProperty] private int _downloadUnitIndex = UnitToIndex(Defaults.DownloadUnit);

    // 各項目の ↑（送信）↓（受信）トグル。null = 選んだプロセスで違う（混在）。SPEC-0002
    [ObservableProperty] private bool? _blockOut = true;
    [ObservableProperty] private bool? _blockIn = true;
    [ObservableProperty] private bool? _resetOut = true;
    [ObservableProperty] private bool? _resetIn = true;
    [ObservableProperty] private bool? _dropOut = true;
    [ObservableProperty] private bool? _dropIn = true;
    [ObservableProperty] private bool? _delayOut = true;
    [ObservableProperty] private bool? _delayIn = true;
    [ObservableProperty] private string _timedBlockSeconds = "5";
    [ObservableProperty] private string _message = string.Empty;

    /// <summary>ホストとロガーを指定して生成する。</summary>
    public ControlPanelViewModel(MonitorHost host, ILoggerFactory loggerFactory)
    {
        _host = host;
        _logger = loggerFactory.CreateLogger<ControlPanelViewModel>();
        _timedBlock = new TimedBlockController(host.Store.SetTimedBlock, host.Time, loggerFactory.CreateLogger<TimedBlockController>());
    }

    /// <summary>制御の対象を切り替える（プロセス一覧の選択に連動。複数可）。対象の現在の設定をパネルに読み込む。</summary>
    public void SetTargets(IReadOnlyList<ProcessRowViewModel> targets)
    {
        _targets = targets;
        HasTarget = targets.Count > 0;

        // 別のプロセスを選んだときだけ前のメッセージを消す。行が一覧から消えて選択が外れた場合（解除直後など）は結果を残す
        if (targets.Count > 0)
        {
            Message = string.Empty;
        }

        var names = targets.Select(t => (t.Name, t.Pid)).ToList();
        TargetText = targets.Count == 0 ? string.Empty : $"Target: {ProcessSelectionText.Short(names)}";
        TargetToolTip = targets.Count > 1 ? ProcessSelectionText.Full(names) : null;
        Load(targets.Count == 0 ? ControlPanelMix.Combine([Defaults]) : ControlPanelMix.Combine(targets.Select(OwnState).ToList()));
    }

    // そのプロセスの今の設定をパネルの形で（制御していなければ既定値）
    private ControlPanelState OwnState(ProcessRowViewModel target) =>
        _host.Store.TryGetConfiguredPolicy(target.Pid, out var policy) ? ControlPolicyEdits.ToPanel(policy, Defaults) : Defaults;

    private void Load(ControlPanelMix m)
    {
        _loading = true;
        try
        {
            BlockOn = m.Block;
            ResetOn = m.Reset;
            DropOn = m.DropOn;
            DropPercent = m.DropPercent;
            DelayOn = m.DelayOn;
            DelayMs = m.DelayMs;
            JitterMs = m.JitterMs;
            UploadOn = m.UploadOn;
            UploadValue = m.Upload;
            UploadUnitIndex = m.UploadUnit is { } uu ? UnitToIndex(uu) : Mixed;
            DownloadOn = m.DownloadOn;
            DownloadValue = m.Download;
            DownloadUnitIndex = m.DownloadUnit is { } du ? UnitToIndex(du) : Mixed;
            BlockOut = m.BlockOut;
            BlockIn = m.BlockIn;
            ResetOut = m.ResetOut;
            ResetIn = m.ResetIn;
            DropOut = m.DropOut;
            DropIn = m.DropIn;
            DelayOut = m.DelayOut;
            DelayIn = m.DelayIn;
        }
        finally
        {
            _loading = false;
        }
    }

    // パネルで決まっている項目（混在でない項目）
    private ControlPanelMix CurrentMix() => new()
    {
        Block = BlockOn,
        Reset = ResetOn,
        DropOn = DropOn,
        DropPercent = DropPercent,
        DelayOn = DelayOn,
        DelayMs = DelayMs,
        JitterMs = JitterMs,
        UploadOn = UploadOn,
        Upload = UploadValue,
        UploadUnit = UploadUnitIndex is >= 0 and var u && u < IndexToUnit.Length ? IndexToUnit[u] : null,
        DownloadOn = DownloadOn,
        Download = DownloadValue,
        DownloadUnit = DownloadUnitIndex is >= 0 and var dn && dn < IndexToUnit.Length ? IndexToUnit[dn] : null,
        BlockOut = BlockOut,
        BlockIn = BlockIn,
        ResetOut = ResetOut,
        ResetIn = ResetIn,
        DropOut = DropOut,
        DropIn = DropIn,
        DelayOut = DelayOut,
        DelayIn = DelayIn,
    };

    // 混在（null）のチェックを押したら、Windows の慣習どおり全員 ON にそろえる。WPF の CheckBox は null から押すと false になるので、
    // ここで true に置き換える（置き換えで再び変更通知が来て、そちらで反映する）。片方だけ Block していた対象の遮断を黙って外さないため
    private bool FromMixedToOn(bool? oldValue, bool? newValue, Action<bool?> set)
    {
        if (!_loading && oldValue is null && newValue == false)
        {
            set(true);
            return true;
        }

        return false;
    }

    // どの項目が変わっても、選択中のプロセスに即時反映する（数値は確定したときに通知が来る）
    partial void OnBlockOnChanged(bool? oldValue, bool? newValue)
    {
        if (!FromMixedToOn(oldValue, newValue, v => BlockOn = v))
        {
            Apply("Block");
        }
    }

    partial void OnResetOnChanged(bool? oldValue, bool? newValue)
    {
        if (!FromMixedToOn(oldValue, newValue, v => ResetOn = v))
        {
            Apply("Reset");
        }
    }

    partial void OnDropOnChanged(bool? oldValue, bool? newValue)
    {
        if (!FromMixedToOn(oldValue, newValue, v => DropOn = v))
        {
            Apply("Drop");
        }
    }

    partial void OnDropPercentChanged(string? value) => ApplyIf(DropOn, "Drop %");
    partial void OnDelayOnChanged(bool? oldValue, bool? newValue)
    {
        if (!FromMixedToOn(oldValue, newValue, v => DelayOn = v))
        {
            Apply("Delay");
        }
    }

    partial void OnDelayMsChanged(string? value) => ApplyIf(DelayOn, "Delay ms");
    partial void OnJitterMsChanged(string? value) => ApplyIf(DelayOn, "Jitter ms");
    partial void OnUploadOnChanged(bool? oldValue, bool? newValue)
    {
        if (!FromMixedToOn(oldValue, newValue, v => UploadOn = v))
        {
            Apply("Upload bandwidth");
        }
    }

    partial void OnUploadValueChanged(string? value) => ApplyIf(UploadOn, "Upload bandwidth value");
    partial void OnUploadUnitIndexChanged(int value) => ApplyIf(UploadOn, "Upload bandwidth unit");
    partial void OnDownloadOnChanged(bool? oldValue, bool? newValue)
    {
        if (!FromMixedToOn(oldValue, newValue, v => DownloadOn = v))
        {
            Apply("Download bandwidth");
        }
    }

    partial void OnDownloadValueChanged(string? value) => ApplyIf(DownloadOn, "Download bandwidth value");
    partial void OnDownloadUnitIndexChanged(int value) => ApplyIf(DownloadOn, "Download bandwidth unit");

    partial void OnBlockOutChanged(bool? oldValue, bool? newValue) => DirectionToggled(oldValue, newValue, BlockIn, v => BlockOut = v, "Block ↑");
    partial void OnBlockInChanged(bool? oldValue, bool? newValue) => DirectionToggled(oldValue, newValue, BlockOut, v => BlockIn = v, "Block ↓");
    partial void OnResetOutChanged(bool? oldValue, bool? newValue) => DirectionToggled(oldValue, newValue, ResetIn, v => ResetOut = v, "Reset ↑");
    partial void OnResetInChanged(bool? oldValue, bool? newValue) => DirectionToggled(oldValue, newValue, ResetOut, v => ResetIn = v, "Reset ↓");
    partial void OnDropOutChanged(bool? oldValue, bool? newValue) => DirectionToggled(oldValue, newValue, DropIn, v => DropOut = v, "Drop ↑");
    partial void OnDropInChanged(bool? oldValue, bool? newValue) => DirectionToggled(oldValue, newValue, DropOut, v => DropIn = v, "Drop ↓");
    partial void OnDelayOutChanged(bool? oldValue, bool? newValue) => DirectionToggled(oldValue, newValue, DelayIn, v => DelayOut = v, "Delay ↑");
    partial void OnDelayInChanged(bool? oldValue, bool? newValue) => DirectionToggled(oldValue, newValue, DelayOut, v => DelayIn = v, "Delay ↓");

    // ↑↓ トグルの変更。最低 1 方向は残す（SPEC-0002 U1 / U2）: 相方が確定 ON のときだけ OFF にできる。
    // 相方が OFF か混在（null = OFF のプロセスがいるかもしれない）なら元に戻して知らせる。
    // 向きは項目の ON/OFF に関係なく反映する（OFF の項目でも向きを覚えておく。U3）
    private void DirectionToggled(bool? oldValue, bool? newValue, bool? sibling, Action<bool?> set, string trigger)
    {
        if (FromMixedToOn(oldValue, newValue, set))
        {
            return;
        }

        if (!_loading && newValue == false && sibling != true)
        {
            // set(true) で同じ OnXxxChanged が再帰的に呼ばれ、その中で（元の値のまま）Apply が 1 回走る。ここでは呼ばない
            set(true);
            Message = "At least one direction must stay on";
            return;
        }

        Apply(trigger);
    }

    // OFF の項目の値だけを変えたときは何も変わらないので反映しない（値を入れてからチェックする使い方のため、入力欄は無効化しない）。
    // 混在（null）のときは、ON のプロセスがあるかもしれないので反映する
    private void ApplyIf(bool? itemOn, string trigger)
    {
        if (itemOn != false)
        {
            Apply(trigger);
        }
    }

    private void Apply(string trigger)
    {
        if (_loading || _targets.Count == 0)
        {
            return;
        }

        var mix = CurrentMix();
        var errors = new List<string>();
        string? lastDescription = null;
        int applied = 0;
        int cleared = 0;
        int failed = 0;
        foreach (var target in _targets)
        {
            // 1 つの対象で失敗しても、残りの対象への反映と制御エンジンの同期は続ける
            try
            {
                _host.Store.TryGetConfiguredPolicy(target.Pid, out var current);
                var targetErrors = new List<string>();
                var next = ControlPolicyEdits.FromPanel(current, mix.ApplyTo(OwnState(target)), targetErrors);
                errors.AddRange(targetErrors.Where(e => !errors.Contains(e)));
                if (next is null)
                {
                    _host.Store.RemovePid(target.Pid);
                    lastDescription = "No control";
                    cleared++;
                    _logger.LogInformation("control cleared ({Trigger}): pid={Pid} name={Name} errors={Errors}", trigger, target.Pid, target.Name, targetErrors.Count);
                }
                else
                {
                    _host.Store.SetPidPolicy(target.Pid, next);
                    lastDescription = ProcessRowViewModel.DescribeControl(next, false);
                    applied++;
                    _logger.LogInformation("control applied ({Trigger}): pid={Pid} name={Name} policy=[{Policy}] errors=[{Errors}]",
                        trigger, target.Pid, target.Name, lastDescription, string.Join("; ", targetErrors));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                failed++;
                string error = $"{target.Name} (PID {target.Pid}): {ex.Message}";
                errors.Add(error);
                _logger.LogWarning(ex, "control apply failed ({Trigger}): pid={Pid} name={Name}", trigger, target.Pid, target.Name);
            }
        }

        _host.SyncControlEngine();
        if (_host.Store.AnyConfigured && !_host.ControlActive)
        {
            Message = "Could not start control (check the status bar and the log)";
            _logger.LogWarning("control applied but engine not active ({Trigger})", trigger);
            return;
        }

        // 反映した内容と、反映できなかった項目の理由を両方出す。複数のときは設定がそれぞれ違いうるので件数だけ出す
        string summary = _targets.Count == 1
            ? (failed > 0 ? "Not applied" : $"Applied: {lastDescription}")
            : string.Join(", ", new[]
            {
                applied > 0 ? $"Applied to {applied}" : null,
                cleared > 0 ? $"no control on {cleared}" : null,
                failed > 0 ? $"failed on {failed}" : null,
            }.Where(t => t is not null)) + $" of {_targets.Count} processes";
        Message = errors.Count > 0 ? $"{summary}\n{string.Join("\n", errors)}" : summary;
    }

    /// <summary>全プロセスの制御と Block の上乗せ（時限・手動）を一度に外す。NetworkTrafficController を閉じずに通信を元に戻すため。</summary>
    [RelayCommand]
    private void ClearAll()
    {
        _timedBlock.Cancel();
        _host.Store.ClearAll();
        _host.SyncControlEngine();
        Load(ControlPanelMix.Combine([Defaults]));
        Message = "Cleared all control";
        _logger.LogInformation("control cleared (Clear all)");
    }

    /// <summary>設定済みの全対象を指定秒数だけ Block して自動復帰する。</summary>
    [RelayCommand]
    private void TimedBlock()
    {
        if (!double.TryParse(TimedBlockSeconds, NumberStyles.Float, CultureInfo.CurrentCulture, out double seconds) || seconds < 0.1 || seconds > 3600)
        {
            Message = "Timed block: enter a number of seconds from 0.1 to 3600";
            return;
        }

        if (!_host.Store.AnyConfigured)
        {
            Message = "Timed block: turn on control for at least one process first (a timed block applies to every controlled target)";
            return;
        }

        _timedBlock.Start(TimeSpan.FromSeconds(seconds));
        Message = $"Started a {seconds:0.#} s timed block";
        _logger.LogInformation("timed block started: {Seconds}s", seconds);
    }

    private static int UnitToIndex(BandwidthUnit unit) => Math.Max(0, Array.IndexOf(IndexToUnit, unit));

    /// <inheritdoc />
    public void Dispose() => _timedBlock.Dispose();
}
