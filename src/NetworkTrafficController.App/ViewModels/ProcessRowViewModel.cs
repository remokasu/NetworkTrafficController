using CommunityToolkit.Mvvm.ComponentModel;
using NetworkTrafficController.Core.Control;

namespace NetworkTrafficController.App.ViewModels;

/// <summary>プロセス一覧の 1 行。値は毎回作り直さず更新して、選択やソート状態を保つ。</summary>
public sealed partial class ProcessRowViewModel : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private double _outRate;
    [ObservableProperty] private double _inRate;
    [ObservableProperty] private long _outTotal;
    [ObservableProperty] private long _inTotal;
    [ObservableProperty] private int _connectionCount;
    [ObservableProperty] private string _control = string.Empty;

    /// <summary>PID（行のキー。不変）。</summary>
    public uint Pid { get; }

    /// <summary>PID を指定して生成する。</summary>
    public ProcessRowViewModel(uint pid)
    {
        Pid = pid;
    }

    /// <summary>行の表示名（UI オートメーション・読み上げでの行の名前になる）。</summary>
    public override string ToString() => $"{Pid} {Name}";

    // ソート・表示用の整形文字列（列の SortMemberPath は生の数値プロパティを使う）
    public string OutRateText => Formatting.Rate(OutRate);
    public string InRateText => Formatting.Rate(InRate);
    public string OutTotalText => Formatting.Bytes(OutTotal);
    public string InTotalText => Formatting.Bytes(InTotal);

    partial void OnOutRateChanged(double value) => OnPropertyChanged(nameof(OutRateText));
    partial void OnInRateChanged(double value) => OnPropertyChanged(nameof(InRateText));
    partial void OnOutTotalChanged(long value) => OnPropertyChanged(nameof(OutTotalText));
    partial void OnInTotalChanged(long value) => OnPropertyChanged(nameof(InTotalText));

    /// <summary>現在のポリシーから「Block / 200KB↓ / Delay」のような制御状態の短い表示を作る。</summary>
    public static string DescribeControl(ControlPolicy? policy, bool blockOverride)
    {
        if (policy is null || (!policy.IsActive && !blockOverride))
        {
            return string.Empty;
        }

        var parts = new List<string>(6);
        if (policy.Block || blockOverride)
        {
            // 上乗せ（時限 / ホットキー）は常に両方向なので印を付けない
            parts.Add(blockOverride && !policy.Block ? "Block (timed)" : "Block" + DirectionSuffix(policy.BlockDirection));
        }

        if (policy.Reset)
        {
            parts.Add("Reset" + DirectionSuffix(policy.ResetDirection));
        }

        if (policy.DropProbability > 0)
        {
            parts.Add($"Drop {policy.DropProbability:P0}" + DirectionSuffix(policy.DropDirection));
        }

        if (policy.HasDelay)
        {
            parts.Add($"Delay {policy.Delay.TotalMilliseconds:F0}±{policy.Jitter.TotalMilliseconds:F0}ms" + DirectionSuffix(policy.DelayDirection));
        }

        if (policy.Upload.HasLimit)
        {
            parts.Add("↑" + LimitText(policy.Upload));
        }

        if (policy.Download.HasLimit)
        {
            parts.Add("↓" + LimitText(policy.Download));
        }

        return string.Join(", ", parts);
    }

    // 片方向だけの項目は ↑ / ↓ を後ろに付ける。両方向は印なし（SPEC-0002）
    private static string DirectionSuffix(ControlDirection direction) => direction switch
    {
        ControlDirection.Outbound => "↑",
        ControlDirection.Inbound => "↓",
        _ => string.Empty,
    };

    // 帯域の表示は、一覧の速度と同じ書式（KB/s・MB/s）に揃える。0 は「停止」
    private static string LimitText(BandwidthLimit limit) => limit.IsStopped ? "stopped" : Formatting.Rate(limit.BytesPerSecond);
}
