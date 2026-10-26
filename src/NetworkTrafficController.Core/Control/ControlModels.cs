using System.Globalization;

namespace NetworkTrafficController.Core.Control;

/// <summary>
/// 制御項目（Block / Reset / Drop / Delay）を適用する方向の集合（用語集「方向」、SPEC-0002）。
/// 帯域制限は Upload / Download で向きが決まるため、この型は使わない。
/// </summary>
[Flags]
public enum ControlDirection
{
    /// <summary>どちらにも適用しない（設定としては無効。<see cref="ControlPolicy.Validate"/> で拒否する）。</summary>
    None = 0,

    /// <summary>送信（上り）に適用する。</summary>
    Outbound = 1,

    /// <summary>受信（下り）に適用する。</summary>
    Inbound = 2,

    /// <summary>両方に適用する。</summary>
    Both = Outbound | Inbound,
}

/// <summary><see cref="ControlDirection"/> の判定ヘルパー。</summary>
public static class ControlDirectionExtensions
{
    /// <summary>パケットの向きがこの集合に含まれるか。</summary>
    public static bool Includes(this ControlDirection set, Mapping.Direction direction) =>
        direction == Mapping.Direction.Outbound
            ? (set & ControlDirection.Outbound) != 0
            : (set & ControlDirection.Inbound) != 0;

    /// <summary>設定として有効な値か（<see cref="ControlDirection.None"/> でなく、<see cref="ControlDirection.Both"/> のビット外を含まない）。</summary>
    public static bool IsValid(this ControlDirection set) =>
        set != ControlDirection.None && (set & ~ControlDirection.Both) == 0;
}

/// <summary>帯域制限の単位。KB/s の K は 1,000（SPEC 決定事項）。</summary>
public enum BandwidthUnit
{
    /// <summary>1,000 バイト/秒。</summary>
    KilobytesPerSecond,

    /// <summary>1,000,000 バイト/秒。</summary>
    MegabytesPerSecond,

    /// <summary>1,000,000 ビット/秒 = 125,000 バイト/秒。</summary>
    MegabitsPerSecond,
}

/// <summary>
/// 帯域制限の指定。3 状態: 制限なし / 停止（0 = 一切通さない）/ レート指定。
/// 「0 は停止、制限なしは別」（SPEC）を型で表す。
/// </summary>
public readonly record struct BandwidthLimit
{
    private BandwidthLimit(bool hasLimit, double bytesPerSecond)
    {
        HasLimit = hasLimit;
        BytesPerSecond = bytesPerSecond;
    }

    /// <summary>制限が有効か（false = 制限なし）。</summary>
    public bool HasLimit { get; }

    /// <summary>制限レート（バイト/秒）。<see cref="HasLimit"/> が true のときのみ意味を持つ。0 = 停止。</summary>
    public double BytesPerSecond { get; }

    /// <summary>停止（0 指定。一切通さない）か。</summary>
    public bool IsStopped => HasLimit && BytesPerSecond <= 0;

    /// <summary>制限なし。</summary>
    public static BandwidthLimit Unlimited => new(false, 0);

    /// <summary>停止（0）。</summary>
    public static BandwidthLimit Stopped => new(true, 0);

    /// <summary>指定できる最小レート（0 = 停止を除く）: 1 KB/s。これ未満は待ち時間が桁あふれしうる。</summary>
    public const double MinBytesPerSecond = 1_000;

    /// <summary>指定できる最大レート: 10 GB/s。</summary>
    public const double MaxBytesPerSecond = 10_000_000_000;

    /// <summary>バイト/秒を指定して制限する。0 は停止。範囲外・非有限値は例外。</summary>
    /// <exception cref="ArgumentOutOfRangeException">0 以外で <see cref="MinBytesPerSecond"/>〜<see cref="MaxBytesPerSecond"/> の範囲外、または非有限・負。</exception>
    public static BandwidthLimit FromBytesPerSecond(double bytesPerSecond)
    {
        if (bytesPerSecond == 0)
        {
            return Stopped;
        }

        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond < MinBytesPerSecond || bytesPerSecond > MaxBytesPerSecond)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerSecond), bytesPerSecond, $"Bandwidth must be 0 (stopped) or {MinBytesPerSecond:F0}-{MaxBytesPerSecond:F0} bytes/s");
        }

        return new(true, bytesPerSecond);
    }

    /// <summary>数値と単位から制限を作る。</summary>
    public static BandwidthLimit From(double value, BandwidthUnit unit) => FromBytesPerSecond(value * BytesPerUnit(unit));

    /// <summary>単位 1 あたりのバイト/秒。</summary>
    public static double BytesPerUnit(BandwidthUnit unit) => unit switch
    {
        BandwidthUnit.KilobytesPerSecond => 1_000,
        BandwidthUnit.MegabytesPerSecond => 1_000_000,
        BandwidthUnit.MegabitsPerSecond => 125_000,
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    /// <summary>"100KB" / "2MB" / "8Mbps" / "unlimited" / "stop" をパースする。</summary>
    public static BandwidthLimit Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string s = text.Trim();
        if (s.Equals("unlimited", StringComparison.OrdinalIgnoreCase) || s.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return Unlimited;
        }

        if (s.Equals("stop", StringComparison.OrdinalIgnoreCase) || s == "0")
        {
            return Stopped;
        }

        (string suffix, BandwidthUnit unit)[] units =
        [
            ("mbps", BandwidthUnit.MegabitsPerSecond),
            ("mb", BandwidthUnit.MegabytesPerSecond),
            ("kb", BandwidthUnit.KilobytesPerSecond),
        ];
        foreach (var (suffix, unit) in units)
        {
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                string number = s[..^suffix.Length].Trim();
                if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v) || v < 0)
                {
                    throw new FormatException($"Invalid bandwidth limit number: '{text}'");
                }

                try
                {
                    return From(v, unit);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    throw new FormatException($"Bandwidth limit out of range: '{text}' ({ex.Message.Split(Environment.NewLine)[0]})");
                }
            }
        }

        throw new FormatException($"Invalid bandwidth limit format: '{text}' (e.g. 100KB, 2MB, 8Mbps, unlimited, stop)");
    }

    /// <inheritdoc />
    public override string ToString() => HasLimit ? (IsStopped ? "stop" : $"{BytesPerSecond:F0} B/s") : "unlimited";
}

/// <summary>
/// 1 つの制御対象に適用する設定。適用順は Block → Reset → Drop % → 帯域制限 → Delay（ADR-0005 §1）。
/// すべて併用可。
/// </summary>
public sealed record ControlPolicy
{
    /// <summary>Delay / jitter に指定できる上限（キューに長時間パケットを抱えないため）。</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

    /// <summary>全パケットを破棄して無音で切断する。</summary>
    public bool Block { get; init; }

    /// <summary>Block を適用する方向（SPEC-0002: 項目ごとに指定。既定は両方）。</summary>
    public ControlDirection BlockDirection { get; init; } = ControlDirection.Both;

    /// <summary>
    /// TCP の接続に RST を注入して即時に切断する（UDP は対象外でそのまま通す。SPEC 決定事項）。
    /// loopback では RST が相手に届かず無音のタイムアウトになるため、loopback の即時切断は <see cref="Block"/> を使う（ADR-0003）。
    /// </summary>
    public bool Reset { get; init; }

    /// <summary>
    /// Reset を適用する方向。どちら向きのパケットをきっかけに RST を返すか（= 自プロセス側と相手側のどちらに RST が届くか）を決める。
    /// TCP 接続は双方向なので、片方向だけでもその接続は結局両端で使えなくなる。
    /// </summary>
    public ControlDirection ResetDirection { get; init; } = ControlDirection.Both;

    /// <summary>確率的に破棄する割合（0〜1）。</summary>
    public double DropProbability { get; init; }

    /// <summary>Drop を適用する方向。</summary>
    public ControlDirection DropDirection { get; init; } = ControlDirection.Both;

    /// <summary>固定遅延。</summary>
    public TimeSpan Delay { get; init; }

    /// <summary>遅延の揺らぎ（±jitter の一様分布）。同じ接続・同じ方向の中では順序を保つ（SPEC 決定事項）。</summary>
    public TimeSpan Jitter { get; init; }

    /// <summary>Delay を適用する方向。</summary>
    public ControlDirection DelayDirection { get; init; } = ControlDirection.Both;

    /// <summary>上り（送信）の帯域制限。</summary>
    public BandwidthLimit Upload { get; init; } = BandwidthLimit.Unlimited;

    /// <summary>下り（受信）の帯域制限。</summary>
    public BandwidthLimit Download { get; init; } = BandwidthLimit.Unlimited;

    /// <summary>遅延（固定 + 揺らぎ）が有効か。</summary>
    public bool HasDelay => Delay > TimeSpan.Zero || Jitter > TimeSpan.Zero;

    /// <summary>何らかの制御が有効か（無効なら制御ハンドルを開く必要がない）。</summary>
    public bool IsActive => Block || Reset || DropProbability > 0 || HasDelay || Upload.HasLimit || Download.HasLimit;

    /// <summary>値の範囲を検証する。</summary>
    /// <exception cref="ArgumentOutOfRangeException">範囲外の値がある場合。</exception>
    public void Validate()
    {
        if (!double.IsFinite(DropProbability) || DropProbability < 0 || DropProbability > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(DropProbability), DropProbability, "Drop % must be between 0 and 100");
        }

        if (Delay < TimeSpan.Zero || Delay > MaxDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(Delay), Delay, $"Delay must be between 0 and {MaxDelay.TotalMilliseconds:F0} ms");
        }

        if (Jitter < TimeSpan.Zero || Jitter > MaxDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(Jitter), Jitter, $"jitter must be between 0 and {MaxDelay.TotalMilliseconds:F0} ms");
        }

        ValidateDirection(BlockDirection, nameof(BlockDirection));
        ValidateDirection(ResetDirection, nameof(ResetDirection));
        ValidateDirection(DropDirection, nameof(DropDirection));
        ValidateDirection(DelayDirection, nameof(DelayDirection));
    }

    private static void ValidateDirection(ControlDirection direction, string name)
    {
        if (!direction.IsValid())
        {
            throw new ArgumentOutOfRangeException(name, direction, "at least one direction (outbound / inbound) is required");
        }
    }

    /// <summary>指定方向の帯域制限。</summary>
    public BandwidthLimit LimitFor(Mapping.Direction direction) =>
        direction == Mapping.Direction.Outbound ? Upload : Download;
}

/// <summary>パケットを破棄した理由（統計を理由別に持つ）。</summary>
public enum DropReason
{
    /// <summary>Block による破棄。</summary>
    Block,

    /// <summary>帯域制限 0（停止）による破棄。</summary>
    BandwidthStopped,

    /// <summary>遅延キューの上限超過による破棄。</summary>
    QueueOverflow,

    /// <summary>Drop % による確率的な破棄。</summary>
    Random,
}

/// <summary>1 パケットの処理結果。</summary>
public enum PacketActionKind
{
    /// <summary>そのまま通す（即再注入）。</summary>
    Pass,

    /// <summary>破棄する。</summary>
    Drop,

    /// <summary>指定時刻まで遅延させてから再注入する。</summary>
    Delay,

    /// <summary>元のパケットを破棄し、送信元へ TCP RST を注入する。</summary>
    Reset,
}

/// <summary>1 パケットに対する制御判定の結果。</summary>
/// <param name="Action">処理。</param>
/// <param name="ReleaseTimestamp">遅延時の解放時刻（<see cref="TimeProvider.GetTimestamp"/> の値）。</param>
/// <param name="DropReason">破棄の理由。</param>
/// <param name="Reserved">帯域制限のキューにバイトを計上したか（解放・破棄時に <see cref="TargetController.OnDequeued"/> で戻す必要がある）。</param>
public readonly record struct PacketDisposition(PacketActionKind Action, long ReleaseTimestamp, DropReason DropReason, bool Reserved = false)
{
    /// <summary>通す。</summary>
    public static PacketDisposition Pass => new(PacketActionKind.Pass, 0, default);

    /// <summary>破棄する。</summary>
    public static PacketDisposition Drop(DropReason reason) => new(PacketActionKind.Drop, 0, reason);

    /// <summary>指定時刻まで遅延させる。</summary>
    public static PacketDisposition Delay(long releaseTimestamp, bool reserved = false) => new(PacketActionKind.Delay, releaseTimestamp, default, reserved);

    /// <summary>RST を注入して元のパケットを破棄する。</summary>
    public static PacketDisposition Reset => new(PacketActionKind.Reset, 0, default);
}
