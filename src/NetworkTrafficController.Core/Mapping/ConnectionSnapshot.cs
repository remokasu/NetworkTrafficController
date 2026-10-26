using System.Buffers.Binary;

namespace NetworkTrafficController.Core.Mapping;

/// <summary>TCP 接続状態（MIB_TCP_STATE）。</summary>
public enum TcpState
{
    /// <summary>不明。</summary>
    Unknown = 0,
    /// <summary>CLOSED。</summary>
    Closed = 1,
    /// <summary>LISTEN。</summary>
    Listen = 2,
    /// <summary>SYN_SENT。</summary>
    SynSent = 3,
    /// <summary>SYN_RCVD。</summary>
    SynReceived = 4,
    /// <summary>ESTABLISHED。</summary>
    Established = 5,
    /// <summary>FIN_WAIT1。</summary>
    FinWait1 = 6,
    /// <summary>FIN_WAIT2。</summary>
    FinWait2 = 7,
    /// <summary>CLOSE_WAIT。</summary>
    CloseWait = 8,
    /// <summary>CLOSING。</summary>
    Closing = 9,
    /// <summary>LAST_ACK。</summary>
    LastAck = 10,
    /// <summary>TIME_WAIT。</summary>
    TimeWait = 11,
    /// <summary>DELETE_TCB。</summary>
    DeleteTcb = 12,
}

/// <summary>GetExtendedTcpTable の 1 行（正規化済み）。</summary>
public readonly record struct TcpConnectionRow(FlowKey Key, uint Pid, TcpState State);

/// <summary>GetExtendedUdpTable の 1 行（ローカル端点と PID のみ。リモートは取れない）。</summary>
public readonly record struct UdpEndpointRow(EndpointKey Key, uint Pid);

/// <summary>既存接続のスナップショットを供給する（テストではフェイクに差し替える）。</summary>
public interface IConnectionSnapshotProvider
{
    /// <summary>IPv4 / IPv6 の TCP 接続一覧を取得する。</summary>
    IReadOnlyList<TcpConnectionRow> GetTcpConnections();

    /// <summary>IPv4 / IPv6 の UDP 端点一覧を取得する。</summary>
    IReadOnlyList<UdpEndpointRow> GetUdpEndpoints();
}

/// <summary>
/// MIB_*TABLE_OWNER_PID のバイト列を行に変換する純粋関数群。テスト可能にするため P/Invoke から分離している。
/// ポートは下位 16 ビットがネットワークバイトオーダー（Microsoft Learn）。
/// </summary>
internal static class IpHelperTables
{
    public const int Tcp4RowSize = 24;
    public const int Tcp6RowSize = 56;
    public const int Udp4RowSize = 12;
    public const int Udp6RowSize = 28;
    private const int HeaderSize = 4;

    public static void ParseTcp4(ReadOnlySpan<byte> table, List<TcpConnectionRow> output)
    {
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        for (int i = 0; i < count; i++)
        {
            var row = table.Slice(HeaderSize + i * Tcp4RowSize, Tcp4RowSize);
            var state = (TcpState)BinaryPrimitives.ReadUInt32LittleEndian(row);
            var local = NetAddress.FromIPv4RawUInt32(BinaryPrimitives.ReadUInt32LittleEndian(row[4..]));
            ushort localPort = PortFromDword(BinaryPrimitives.ReadUInt32LittleEndian(row[8..]));
            var remote = NetAddress.FromIPv4RawUInt32(BinaryPrimitives.ReadUInt32LittleEndian(row[12..]));
            ushort remotePort = PortFromDword(BinaryPrimitives.ReadUInt32LittleEndian(row[16..]));
            uint pid = BinaryPrimitives.ReadUInt32LittleEndian(row[20..]);
            output.Add(new TcpConnectionRow(new FlowKey(IpProtocol.Tcp, local, localPort, remote, remotePort), pid, state));
        }
    }

    public static void ParseTcp6(ReadOnlySpan<byte> table, List<TcpConnectionRow> output)
    {
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        for (int i = 0; i < count; i++)
        {
            var row = table.Slice(HeaderSize + i * Tcp6RowSize, Tcp6RowSize);
            var local = NetAddress.FromBytes(row[..16]);
            ushort localPort = PortFromDword(BinaryPrimitives.ReadUInt32LittleEndian(row[20..]));
            var remote = NetAddress.FromBytes(row.Slice(24, 16));
            ushort remotePort = PortFromDword(BinaryPrimitives.ReadUInt32LittleEndian(row[44..]));
            var state = (TcpState)BinaryPrimitives.ReadUInt32LittleEndian(row[48..]);
            uint pid = BinaryPrimitives.ReadUInt32LittleEndian(row[52..]);
            output.Add(new TcpConnectionRow(new FlowKey(IpProtocol.Tcp, local, localPort, remote, remotePort), pid, state));
        }
    }

    public static void ParseUdp4(ReadOnlySpan<byte> table, List<UdpEndpointRow> output)
    {
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        for (int i = 0; i < count; i++)
        {
            var row = table.Slice(HeaderSize + i * Udp4RowSize, Udp4RowSize);
            var local = NetAddress.FromIPv4RawUInt32(BinaryPrimitives.ReadUInt32LittleEndian(row));
            ushort localPort = PortFromDword(BinaryPrimitives.ReadUInt32LittleEndian(row[4..]));
            uint pid = BinaryPrimitives.ReadUInt32LittleEndian(row[8..]);
            output.Add(new UdpEndpointRow(new EndpointKey(IpProtocol.Udp, local, localPort), pid));
        }
    }

    public static void ParseUdp6(ReadOnlySpan<byte> table, List<UdpEndpointRow> output)
    {
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        for (int i = 0; i < count; i++)
        {
            var row = table.Slice(HeaderSize + i * Udp6RowSize, Udp6RowSize);
            var local = NetAddress.FromBytes(row[..16]);
            ushort localPort = PortFromDword(BinaryPrimitives.ReadUInt32LittleEndian(row[20..]));
            uint pid = BinaryPrimitives.ReadUInt32LittleEndian(row[24..]);
            output.Add(new UdpEndpointRow(new EndpointKey(IpProtocol.Udp, local, localPort), pid));
        }
    }

    /// <summary>dwLocalPort（下位 16 ビットがネットワークバイトオーダー）をホストオーダーのポートに変換する。</summary>
    public static ushort PortFromDword(uint dword) => (ushort)(((dword & 0xFF) << 8) | ((dword >> 8) & 0xFF));
}
