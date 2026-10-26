using System.Globalization;

namespace NetworkTrafficController.Core.Control;

/// <summary>
/// 制御パネルの入力状態（GUI 非依存）。各項目は「ON/OFF + 値の文字列 + 単位」で持ち、文字列の解釈は
/// <see cref="ControlPolicyEdits.FromPanel"/> が行う（入力途中や不正な値でも GUI 側で例外にしないため）。
/// </summary>
public sealed record ControlPanelState
{
    /// <summary>Block。</summary>
    public bool Block { get; init; }

    /// <summary>Reset。</summary>
    public bool Reset { get; init; }

    /// <summary>Drop % を使うか。</summary>
    public bool DropOn { get; init; }

    /// <summary>Drop の割合（%、0〜100）。</summary>
    public string DropPercent { get; init; } = string.Empty;

    /// <summary>Delay を使うか。</summary>
    public bool DelayOn { get; init; }

    /// <summary>固定遅延（ms）。</summary>
    public string DelayMs { get; init; } = string.Empty;

    /// <summary>揺らぎ（ms、空なら 0）。</summary>
    public string JitterMs { get; init; } = string.Empty;

    /// <summary>上り帯域の制限を使うか（false = 制限なし）。</summary>
    public bool UploadOn { get; init; }

    /// <summary>上り帯域の値（0 = 停止）。</summary>
    public string Upload { get; init; } = string.Empty;

    /// <summary>上り帯域の単位。</summary>
    public BandwidthUnit UploadUnit { get; init; } = BandwidthUnit.KilobytesPerSecond;

    /// <summary>下り帯域の制限を使うか。</summary>
    public bool DownloadOn { get; init; }

    /// <summary>下り帯域の値（0 = 停止）。</summary>
    public string Download { get; init; } = string.Empty;

    /// <summary>下り帯域の単位。</summary>
    public BandwidthUnit DownloadUnit { get; init; } = BandwidthUnit.KilobytesPerSecond;

    /// <summary>Block を送信に適用するか（↑ トグル）。</summary>
    public bool BlockOut { get; init; } = true;

    /// <summary>Block を受信に適用するか（↓ トグル）。</summary>
    public bool BlockIn { get; init; } = true;

    /// <summary>Reset を送信に適用するか。</summary>
    public bool ResetOut { get; init; } = true;

    /// <summary>Reset を受信に適用するか。</summary>
    public bool ResetIn { get; init; } = true;

    /// <summary>Drop を送信に適用するか。</summary>
    public bool DropOut { get; init; } = true;

    /// <summary>Drop を受信に適用するか。</summary>
    public bool DropIn { get; init; } = true;

    /// <summary>Delay を送信に適用するか。</summary>
    public bool DelayOut { get; init; } = true;

    /// <summary>Delay を受信に適用するか。</summary>
    public bool DelayIn { get; init; } = true;
}

/// <summary>GUI などからのポリシー編集の純粋ロジック。</summary>
public static class ControlPolicyEdits
{
    /// <summary>
    /// パネルの状態からポリシーを作る。ON の項目のうち値が不正なものは、<paramref name="current"/> の同じ項目の設定を
    /// そのまま使い（未設定なら OFF 扱い）、<paramref name="errors"/> に理由を入れる。不正な項目があっても他の項目
    /// （Block など）は反映する。
    /// </summary>
    /// <returns>新しいポリシー。有効な項目が 1 つも無い場合は null（= 制御を解除すべき）。</returns>
    public static ControlPolicy? FromPanel(ControlPolicy? current, ControlPanelState state, List<string> errors)
    {
        var baseline = current ?? new ControlPolicy();
        var next = new ControlPolicy { Block = state.Block, Reset = state.Reset };

        // 向きは項目の ON/OFF に関係なく保持する（SPEC-0002 U3）。両方 OFF は GUI で防ぐが、来たら前の値を残す
        next = next with
        {
            BlockDirection = DirectionOrKeep(state.BlockOut, state.BlockIn, baseline.BlockDirection, errors, "Block direction"),
            ResetDirection = DirectionOrKeep(state.ResetOut, state.ResetIn, baseline.ResetDirection, errors, "Reset direction"),
            DropDirection = DirectionOrKeep(state.DropOut, state.DropIn, baseline.DropDirection, errors, "Drop direction"),
            DelayDirection = DirectionOrKeep(state.DelayOut, state.DelayIn, baseline.DelayDirection, errors, "Delay direction"),
        };

        if (state.DropOn)
        {
            next = TryNumber(state.DropPercent, 0, 100, out double pct)
                ? next with { DropProbability = pct / 100.0 }
                : Keep(next with { DropProbability = baseline.DropProbability }, errors, "Drop", "enter a number from 0 to 100");
        }

        if (state.DelayOn)
        {
            double maxMs = ControlPolicy.MaxDelay.TotalMilliseconds;
            bool delayOk = TryNumber(state.DelayMs, 0, maxMs, out double delay);
            bool jitterOk = TryNumberOrEmpty(state.JitterMs, 0, maxMs, out double jitter);
            next = delayOk && jitterOk
                ? next with { Delay = TimeSpan.FromMilliseconds(delay), Jitter = TimeSpan.FromMilliseconds(jitter) }
                : Keep(next with { Delay = baseline.Delay, Jitter = baseline.Jitter }, errors, "Delay", $"enter a number from 0 to {maxMs:F0} ms");
        }

        if (state.UploadOn)
        {
            next = TryBandwidth(state.Upload, state.UploadUnit, out var up, out string? why)
                ? next with { Upload = up }
                : Keep(next with { Upload = baseline.Upload }, errors, "Upload bandwidth", why!);
        }

        if (state.DownloadOn)
        {
            next = TryBandwidth(state.Download, state.DownloadUnit, out var down, out string? why)
                ? next with { Download = down }
                : Keep(next with { Download = baseline.Download }, errors, "Download bandwidth", why!);
        }

        return next.IsActive ? next : null;
    }

    /// <summary>ポリシーをパネルの状態に変換する（対象を選び直したときの表示用）。</summary>
    public static ControlPanelState ToPanel(ControlPolicy policy, ControlPanelState defaults)
    {
        var upUnit = defaults.UploadUnit;
        string up = policy.Upload.HasLimit ? LimitValue(policy.Upload, out upUnit) : defaults.Upload;
        var downUnit = defaults.DownloadUnit;
        string down = policy.Download.HasLimit ? LimitValue(policy.Download, out downUnit) : defaults.Download;

        return defaults with
        {
            Block = policy.Block,
            Reset = policy.Reset,
            DropOn = policy.DropProbability > 0,
            DropPercent = policy.DropProbability > 0 ? Format(policy.DropProbability * 100) : defaults.DropPercent,
            DelayOn = policy.HasDelay,
            DelayMs = policy.HasDelay ? Format(policy.Delay.TotalMilliseconds) : defaults.DelayMs,
            JitterMs = policy.HasDelay ? Format(policy.Jitter.TotalMilliseconds) : defaults.JitterMs,
            UploadOn = policy.Upload.HasLimit,
            Upload = up,
            UploadUnit = upUnit,
            DownloadOn = policy.Download.HasLimit,
            Download = down,
            DownloadUnit = downUnit,
            BlockOut = policy.BlockDirection.Includes(Mapping.Direction.Outbound),
            BlockIn = policy.BlockDirection.Includes(Mapping.Direction.Inbound),
            ResetOut = policy.ResetDirection.Includes(Mapping.Direction.Outbound),
            ResetIn = policy.ResetDirection.Includes(Mapping.Direction.Inbound),
            DropOut = policy.DropDirection.Includes(Mapping.Direction.Outbound),
            DropIn = policy.DropDirection.Includes(Mapping.Direction.Inbound),
            DelayOut = policy.DelayDirection.Includes(Mapping.Direction.Outbound),
            DelayIn = policy.DelayDirection.Includes(Mapping.Direction.Inbound),
        };
    }

    private static ControlPolicy Keep(ControlPolicy kept, List<string> errors, string item, string reason)
    {
        errors.Add($"{item}: {reason} (kept the previous value)");
        return kept;
    }

    // ↑↓ の 2 つのトグルを方向の集合にする。両方 OFF は無効なので前の値を残し、理由を errors に入れる
    private static ControlDirection DirectionOrKeep(bool outbound, bool inbound, ControlDirection previous, List<string> errors, string item)
    {
        var direction = (outbound ? ControlDirection.Outbound : ControlDirection.None)
                      | (inbound ? ControlDirection.Inbound : ControlDirection.None);
        if (direction.IsValid())
        {
            return direction;
        }

        errors.Add($"{item}: at least one direction is required (kept the previous value)");
        return previous;
    }

    private static bool TryBandwidth(string text, BandwidthUnit unit, out BandwidthLimit limit, out string? reason)
    {
        limit = BandwidthLimit.Unlimited;
        reason = null;
        if (!TryNumber(text, 0, double.MaxValue, out double value))
        {
            reason = "enter a number of 0 or more (0 = stopped)";
            return false;
        }

        try
        {
            limit = BandwidthLimit.From(value, unit);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            reason = "enter 0 (stopped) or a value from 1 KB/s to 10 GB/s";
            return false;
        }
    }

    // 表示は、1 MB/s 以上で割り切れるなら MB/s、それ以外は KB/s にする
    private static string LimitValue(BandwidthLimit limit, out BandwidthUnit unit)
    {
        if (limit.IsStopped)
        {
            unit = BandwidthUnit.KilobytesPerSecond;
            return "0";
        }

        double mb = limit.BytesPerSecond / BandwidthLimit.BytesPerUnit(BandwidthUnit.MegabytesPerSecond);
        if (mb >= 1 && Math.Abs(mb - Math.Round(mb, 3)) < 1e-9)
        {
            unit = BandwidthUnit.MegabytesPerSecond;
            return Format(mb);
        }

        unit = BandwidthUnit.KilobytesPerSecond;
        return Format(limit.BytesPerSecond / BandwidthLimit.BytesPerUnit(BandwidthUnit.KilobytesPerSecond));
    }

    private static bool TryNumberOrEmpty(string text, double min, double max, out double value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = 0;
            return true;
        }

        return TryNumber(text, min, max, out value);
    }

    private static bool TryNumber(string text, double min, double max, out double value)
    {
        string s = (text ?? string.Empty).Trim();
        bool ok = double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        return ok && double.IsFinite(value) && value >= min && value <= max;
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
