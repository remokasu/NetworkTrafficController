using System.Buffers.Binary;
using NetworkTrafficController.Core.Mapping;

namespace NetworkTrafficController.Core.Monitoring;

/// <summary>生パケットから取り出したヘッダ情報。</summary>
public readonly record struct ParsedPacket(
    bool IsIPv6,
    byte Protocol,
    NetAddress Source,
    ushort SourcePort,
    NetAddress Destination,
    ushort DestinationPort,
    int TotalLength)
{
    /// <summary>TCP / UDP のどちらかか（ポートが有効か）。</summary>
    public bool HasPorts => Protocol is IpProtocol.Tcp or IpProtocol.Udp;
}

/// <summary>
/// IPv4 / IPv6 ヘッダと TCP / UDP のポートをマネージドコードで解析する。
/// WinDivert のバッチ受信ではパケットが連結して届くため、<see cref="ParsedPacket.TotalLength"/> で次のパケットに進む。
/// </summary>
public static class PacketParser
{
    private const int IPv4MinHeader = 20;
    private const int IPv6Header = 40;
    private const int PortsLength = 4;

    /// <summary>先頭のパケットを解析する。不正なパケットは false。</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out ParsedPacket packet)
    {
        packet = default;
        if (data.Length < 1)
        {
            return false;
        }

        int version = data[0] >> 4;
        return version switch
        {
            4 => TryParseIPv4(data, out packet),
            6 => TryParseIPv6(data, out packet),
            _ => false,
        };
    }

    private static bool TryParseIPv4(ReadOnlySpan<byte> data, out ParsedPacket packet)
    {
        packet = default;
        if (data.Length < IPv4MinHeader)
        {
            return false;
        }

        int headerLength = (data[0] & 0x0F) * 4;
        int totalLength = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (headerLength < IPv4MinHeader || totalLength < headerLength || totalLength > data.Length)
        {
            return false;
        }

        byte protocol = data[9];
        var src = NetAddress.FromIPv4Bytes(data.Slice(12, 4));
        var dst = NetAddress.FromIPv4Bytes(data.Slice(16, 4));
        int fragmentOffset = BinaryPrimitives.ReadUInt16BigEndian(data[6..]) & 0x1FFF;
        ReadPorts(data.Slice(headerLength, totalLength - headerLength), protocol, fragmentOffset == 0, out ushort srcPort, out ushort dstPort);
        packet = new ParsedPacket(false, protocol, src, srcPort, dst, dstPort, totalLength);
        return true;
    }

    private static bool TryParseIPv6(ReadOnlySpan<byte> data, out ParsedPacket packet)
    {
        packet = default;
        if (data.Length < IPv6Header)
        {
            return false;
        }

        int payloadLength = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        int totalLength = IPv6Header + payloadLength;
        if (totalLength > data.Length)
        {
            return false;
        }

        byte nextHeader = data[6];
        var src = NetAddress.FromBytes(data.Slice(8, 16));
        var dst = NetAddress.FromBytes(data.Slice(24, 16));

        int offset = IPv6Header;
        bool firstFragment = true;
        while (true)
        {
            if (nextHeader is 0 or 43 or 60) // Hop-by-Hop / Routing / Destination Options
            {
                if (offset + 2 > totalLength)
                {
                    return false;
                }

                int extLength = (data[offset + 1] + 1) * 8;
                nextHeader = data[offset];
                offset += extLength;
                continue;
            }

            if (nextHeader == 44) // Fragment
            {
                if (offset + 8 > totalLength)
                {
                    return false;
                }

                firstFragment = (BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]) & 0xFFF8) == 0;
                nextHeader = data[offset];
                offset += 8;
                continue;
            }

            if (nextHeader == 51) // Authentication Header
            {
                if (offset + 2 > totalLength)
                {
                    return false;
                }

                int ahLength = (data[offset + 1] + 2) * 4;
                nextHeader = data[offset];
                offset += ahLength;
                continue;
            }

            break;
        }

        if (offset > totalLength)
        {
            return false;
        }

        ReadPorts(data[offset..totalLength], nextHeader, firstFragment, out ushort srcPort, out ushort dstPort);
        packet = new ParsedPacket(true, nextHeader, src, srcPort, dst, dstPort, totalLength);
        return true;
    }

    private static void ReadPorts(ReadOnlySpan<byte> transport, byte protocol, bool firstFragment, out ushort srcPort, out ushort dstPort)
    {
        srcPort = 0;
        dstPort = 0;
        if (!firstFragment || protocol is not (IpProtocol.Tcp or IpProtocol.Udp) || transport.Length < PortsLength)
        {
            return;
        }

        srcPort = BinaryPrimitives.ReadUInt16BigEndian(transport);
        dstPort = BinaryPrimitives.ReadUInt16BigEndian(transport[2..]);
    }
}
