using System.Globalization;

namespace NetworkTrafficController.Core.Monitoring;

/// <summary>
/// プロセス一覧の絞り込み。名前は大文字小文字を区別しない部分一致、PID は 10 進表記の部分一致で、どちらかに当たれば表示する。
/// </summary>
public static class ProcessFilter
{
    /// <summary>絞り込み文字列に行が一致するか。空（空白だけ）なら全件一致。前後の空白は無視する。</summary>
    public static bool Matches(string? filter, string name, uint pid)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        string term = filter.Trim();
        return name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || pid.ToString(CultureInfo.InvariantCulture).Contains(term, StringComparison.Ordinal);
    }
}
