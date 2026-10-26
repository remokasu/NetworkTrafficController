using System.Globalization;

namespace NetworkTrafficController.App;

/// <summary>バイト数・速度の表示整形（10 進接頭辞。KB = 1,000 B、SPEC 決定事項に合わせる）。</summary>
public static class Formatting
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>バイト数を "1.2 MB" のように整形する。</summary>
    public static string Bytes(double value)
    {
        double v = Math.Abs(value);
        int unit = 0;
        while (v >= 1000 && unit < Units.Length - 1)
        {
            v /= 1000;
            unit++;
        }

        string sign = value < 0 ? "-" : string.Empty;
        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{sign}{v:F0} {Units[unit]}")
            : string.Create(CultureInfo.InvariantCulture, $"{sign}{v:F1} {Units[unit]}");
    }

    /// <summary>速度を "1.2 MB/s" のように整形する。</summary>
    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";
}
