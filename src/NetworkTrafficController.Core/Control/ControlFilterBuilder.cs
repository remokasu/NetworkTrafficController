using System.Text;

namespace NetworkTrafficController.Core.Control;

/// <summary>
/// 制御ハンドル（NETWORK 通常モード）のフィルタ文字列を、対象プロセスのローカルポートから作る（ADR-0002 B-3）。
/// NETWORK レイヤーは local/remote を区別しないため、送信側は SrcPort、受信側（loopback 含む）は DstPort に
/// 現れることを踏まえ、両方で照合する。同じポートを使う他プロセスのパケットも拾うが、ユーザーモードで
/// PID 照合してそのまま通すので害は遅延だけ（fail-open）。
/// </summary>
public static class ControlFilterBuilder
{
    /// <summary>すべての TCP / UDP を対象にする広いフィルタ（ポート不明・上限超過時のフォールバック）。</summary>
    public const string CatchAll = "tcp or udp";

    /// <summary>ポート集合からフィルタを組み立てる。空または過多なら <see cref="CatchAll"/> を返す。</summary>
    public static string Build(IReadOnlyList<ushort> tcpPorts, IReadOnlyList<ushort> udpPorts)
    {
        if (tcpPorts.Count == 0 && udpPorts.Count == 0)
        {
            return CatchAll;
        }

        var clauses = new List<string>(2);
        AppendProtocol(clauses, "tcp", tcpPorts);
        AppendProtocol(clauses, "udp", udpPorts);
        return string.Join(" or ", clauses);
    }

    private static void AppendProtocol(List<string> clauses, string proto, IReadOnlyList<ushort> ports)
    {
        if (ports.Count == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.Append('(').Append(proto).Append(" and (");
        for (int i = 0; i < ports.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(" or ");
            }

            sb.Append(proto).Append(".SrcPort == ").Append(ports[i]).Append(" or ").Append(proto).Append(".DstPort == ").Append(ports[i]);
        }

        sb.Append("))");
        clauses.Add(sb.ToString());
    }
}
