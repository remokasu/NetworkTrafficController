namespace NetworkTrafficController.Core.Control;

/// <summary>
/// 複数のプロセスを選んだときの制御パネルの表示。各項目は、選んだ全員で同じならその値、違えば null（混在）。
/// パネルでは混在のチェックを中間の状態で、混在の数値を空欄で出す。
/// </summary>
public sealed record ControlPanelMix
{
    /// <summary>Block。</summary>
    public bool? Block { get; init; }

    /// <summary>Reset。</summary>
    public bool? Reset { get; init; }

    /// <summary>Drop を使うか。</summary>
    public bool? DropOn { get; init; }

    /// <summary>Drop の割合。</summary>
    public string? DropPercent { get; init; }

    /// <summary>Delay を使うか。</summary>
    public bool? DelayOn { get; init; }

    /// <summary>固定遅延。</summary>
    public string? DelayMs { get; init; }

    /// <summary>揺らぎ。</summary>
    public string? JitterMs { get; init; }

    /// <summary>上り帯域の制限を使うか。</summary>
    public bool? UploadOn { get; init; }

    /// <summary>上り帯域の値。</summary>
    public string? Upload { get; init; }

    /// <summary>上り帯域の単位。</summary>
    public BandwidthUnit? UploadUnit { get; init; }

    /// <summary>下り帯域の制限を使うか。</summary>
    public bool? DownloadOn { get; init; }

    /// <summary>下り帯域の値。</summary>
    public string? Download { get; init; }

    /// <summary>下り帯域の単位。</summary>
    public BandwidthUnit? DownloadUnit { get; init; }

    /// <summary>Block の ↑。</summary>
    public bool? BlockOut { get; init; }

    /// <summary>Block の ↓。</summary>
    public bool? BlockIn { get; init; }

    /// <summary>Reset の ↑。</summary>
    public bool? ResetOut { get; init; }

    /// <summary>Reset の ↓。</summary>
    public bool? ResetIn { get; init; }

    /// <summary>Drop の ↑。</summary>
    public bool? DropOut { get; init; }

    /// <summary>Drop の ↓。</summary>
    public bool? DropIn { get; init; }

    /// <summary>Delay の ↑。</summary>
    public bool? DelayOut { get; init; }

    /// <summary>Delay の ↓。</summary>
    public bool? DelayIn { get; init; }

    /// <summary>
    /// 選んだプロセスそれぞれの状態をまとめる。全員で同じ項目はその値、違う項目は null にする。
    /// </summary>
    public static ControlPanelMix Combine(IReadOnlyList<ControlPanelState> states)
    {
        ArgumentOutOfRangeException.ThrowIfZero(states.Count);
        return new ControlPanelMix
        {
            Block = Common(states, s => s.Block),
            Reset = Common(states, s => s.Reset),
            DropOn = Common(states, s => s.DropOn),
            DropPercent = CommonText(states, s => s.DropPercent),
            DelayOn = Common(states, s => s.DelayOn),
            DelayMs = CommonText(states, s => s.DelayMs),
            JitterMs = CommonText(states, s => s.JitterMs),
            UploadOn = Common(states, s => s.UploadOn),
            Upload = CommonText(states, s => s.Upload),
            UploadUnit = Common(states, s => s.UploadUnit),
            DownloadOn = Common(states, s => s.DownloadOn),
            Download = CommonText(states, s => s.Download),
            DownloadUnit = Common(states, s => s.DownloadUnit),
            BlockOut = Common(states, s => s.BlockOut),
            BlockIn = Common(states, s => s.BlockIn),
            ResetOut = Common(states, s => s.ResetOut),
            ResetIn = Common(states, s => s.ResetIn),
            DropOut = Common(states, s => s.DropOut),
            DropIn = Common(states, s => s.DropIn),
            DelayOut = Common(states, s => s.DelayOut),
            DelayIn = Common(states, s => s.DelayIn),
        };
    }

    /// <summary>
    /// 1 つのプロセスの状態に、パネルで決まっている項目（null でない項目）だけを上書きする。
    /// 混在のまま触っていない項目は、そのプロセス自身の設定を残す。
    /// </summary>
    public ControlPanelState ApplyTo(ControlPanelState own) => own with
    {
        Block = Block ?? own.Block,
        Reset = Reset ?? own.Reset,
        DropOn = DropOn ?? own.DropOn,
        DropPercent = DropPercent ?? own.DropPercent,
        DelayOn = DelayOn ?? own.DelayOn,
        DelayMs = DelayMs ?? own.DelayMs,
        JitterMs = JitterMs ?? own.JitterMs,
        UploadOn = UploadOn ?? own.UploadOn,
        Upload = Upload ?? own.Upload,
        UploadUnit = UploadUnit ?? own.UploadUnit,
        DownloadOn = DownloadOn ?? own.DownloadOn,
        Download = Download ?? own.Download,
        DownloadUnit = DownloadUnit ?? own.DownloadUnit,
        BlockOut = BlockOut ?? own.BlockOut,
        BlockIn = BlockIn ?? own.BlockIn,
        ResetOut = ResetOut ?? own.ResetOut,
        ResetIn = ResetIn ?? own.ResetIn,
        DropOut = DropOut ?? own.DropOut,
        DropIn = DropIn ?? own.DropIn,
        DelayOut = DelayOut ?? own.DelayOut,
        DelayIn = DelayIn ?? own.DelayIn,
    };

    private static T? Common<T>(IReadOnlyList<ControlPanelState> states, Func<ControlPanelState, T> get)
        where T : struct
    {
        T first = get(states[0]);
        for (int i = 1; i < states.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(get(states[i]), first))
            {
                return null;
            }
        }

        return first;
    }

    private static string? CommonText(IReadOnlyList<ControlPanelState> states, Func<ControlPanelState, string> get)
    {
        string first = get(states[0]);
        for (int i = 1; i < states.Count; i++)
        {
            if (!string.Equals(get(states[i]), first, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return first;
    }
}
