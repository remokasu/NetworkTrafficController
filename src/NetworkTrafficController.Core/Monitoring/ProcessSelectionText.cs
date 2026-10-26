using System.Globalization;
using System.Text;

namespace NetworkTrafficController.Core.Monitoring;

/// <summary>
/// 選んだプロセスの名前の表示（グラフの見出しと、制御パネルの対象の表示で共通）。
/// 1 つなら「名前 (PID n)」、複数なら名前だけを並べ、同じ名前は「×件数」にまとめ、4 つ以上は「+N more」で省く。
/// </summary>
public static class ProcessSelectionText
{
    /// <summary>名前を並べるのは、まとめた後の名前で 3 つまで。それを超えたら先頭 2 つと残りの件数にする。</summary>
    public const int MaxNames = 3;

    /// <summary>短い表示。空なら空文字。</summary>
    public static string Short(IReadOnlyList<(string Name, uint Pid)> processes)
    {
        if (processes.Count == 0)
        {
            return string.Empty;
        }

        if (processes.Count == 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{processes[0].Name} (PID {processes[0].Pid})");
        }

        // 最初に出てきた順を保ったまま、同じ名前をまとめる
        var groups = new List<(string Name, int Count)>();
        foreach (var (name, _) in processes)
        {
            int index = groups.FindIndex(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                groups.Add((name, 1));
            }
            else
            {
                groups[index] = (groups[index].Name, groups[index].Count + 1);
            }
        }

        if (groups.Count <= MaxNames)
        {
            return string.Join(", ", groups.Select(Label));
        }

        var shown = groups.Take(MaxNames - 1).ToList();
        int rest = processes.Count - shown.Sum(g => g.Count);
        return string.Create(CultureInfo.InvariantCulture, $"{string.Join(", ", shown.Select(Label))} +{rest} more");
    }

    /// <summary>全部の一覧（ツールチップ用）。1 行に 1 プロセスを PID 付きで出す。</summary>
    public static string Full(IReadOnlyList<(string Name, uint Pid)> processes)
    {
        var sb = new StringBuilder();
        foreach (var (name, pid) in processes)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(CultureInfo.InvariantCulture, $"{name} (PID {pid})");
        }

        return sb.ToString();
    }

    private static string Label((string Name, int Count) g) =>
        g.Count == 1 ? g.Name : string.Create(CultureInfo.InvariantCulture, $"{g.Name} ×{g.Count}");
}
