namespace NetworkTrafficController.Core.Mapping;

/// <summary>IP プロトコル番号（照合対象は TCP / UDP のみ）。</summary>
public static class IpProtocol
{
    /// <summary>TCP (6)。</summary>
    public const byte Tcp = 6;

    /// <summary>UDP (17)。</summary>
    public const byte Udp = 17;

    /// <summary>表示名。</summary>
    public static string Name(byte protocol) => protocol switch
    {
        Tcp => "TCP",
        Udp => "UDP",
        _ => protocol.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>パケットの方向（WinDivert の Outbound / Inbound）。用語集「方向」を参照。</summary>
public enum Direction
{
    /// <summary>送信（上り）。</summary>
    Outbound,

    /// <summary>受信（下り）。</summary>
    Inbound,
}

/// <summary>
/// 5-tuple（プロトコル、ローカル端点、リモート端点）。常に local / remote に正規化して持ち、
/// パケットの src / dst は方向で読み替える（ADR-0001）。ポートはホストバイトオーダー。
/// </summary>
public readonly record struct FlowKey(byte Protocol, NetAddress LocalAddress, ushort LocalPort, NetAddress RemoteAddress, ushort RemotePort)
{
    /// <summary>ローカル端点だけのキー。</summary>
    public EndpointKey Local => new(Protocol, LocalAddress, LocalPort);

    /// <summary>local と remote を入れ替えたキー（loopback の受信側照合に使う）。</summary>
    public FlowKey Reversed => new(Protocol, RemoteAddress, RemotePort, LocalAddress, LocalPort);

    /// <inheritdoc />
    public override string ToString() => $"{IpProtocol.Name(Protocol)} {LocalAddress}:{LocalPort} -> {RemoteAddress}:{RemotePort}";
}

/// <summary>ローカル端点のキー（プロトコル、ローカルアドレス、ローカルポート）。Endpoint 表のキー。</summary>
public readonly record struct EndpointKey(byte Protocol, NetAddress Address, ushort Port)
{
    /// <inheritdoc />
    public override string ToString() => $"{IpProtocol.Name(Protocol)} {Address}:{Port}";
}
