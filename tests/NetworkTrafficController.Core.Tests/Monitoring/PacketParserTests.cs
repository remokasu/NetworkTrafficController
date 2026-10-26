using System.Buffers.Binary;
using NetworkTrafficController.Core.Mapping;
using NetworkTrafficController.Core.Monitoring;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Monitoring;

public class PacketParserTests
{
    internal static byte[] IPv4(byte protocol, byte[] src, byte[] dst, ushort srcPort, ushort dstPort, int payload, int options = 0)
    {
        int headerLength = 20 + options;
        int transportLength = protocol == IpProtocol.Tcp ? 20 : 8;
        int total = headerLength + transportLength + payload;
        var p = new byte[total];
        p[0] = (byte)(0x40 | (headerLength / 4));
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)total);
        p[8] = 64;
        p[9] = protocol;
        src.CopyTo(p, 12);
        dst.CopyTo(p, 16);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(headerLength), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(headerLength + 2), dstPort);
        if (protocol == IpProtocol.Tcp)
        {
            p[headerLength + 12] = 0x50; // data offset 5
        }

        return p;
    }

    internal static byte[] IPv6(byte nextHeader, byte[] src, byte[] dst, ushort srcPort, ushort dstPort, int payload, bool hopByHop = false)
    {
        int ext = hopByHop ? 8 : 0;
        int transportLength = nextHeader == IpProtocol.Tcp ? 20 : 8;
        int payloadLength = ext + transportLength + payload;
        var p = new byte[40 + payloadLength];
        p[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), (ushort)payloadLength);
        p[6] = hopByHop ? (byte)0 : nextHeader;
        p[7] = 64;
        src.CopyTo(p, 8);
        dst.CopyTo(p, 24);
        int offset = 40;
        if (hopByHop)
        {
            p[40] = nextHeader; // next header
            p[41] = 0;          // length: (0+1)*8 = 8 bytes
            offset += 8;
        }

        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(offset), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(offset + 2), dstPort);
        return p;
    }

    [Fact]
    public void ParsesIPv4Tcp()
    {
        var bytes = IPv4(IpProtocol.Tcp, [10, 0, 0, 1], [10, 0, 0, 2], 50000, 443, 100);
        Assert.True(PacketParser.TryParse(bytes, out var p));
        Assert.False(p.IsIPv6);
        Assert.Equal(IpProtocol.Tcp, p.Protocol);
        Assert.Equal("10.0.0.1", p.Source.ToString());
        Assert.Equal("10.0.0.2", p.Destination.ToString());
        Assert.Equal(50000, p.SourcePort);
        Assert.Equal(443, p.DestinationPort);
        Assert.Equal(bytes.Length, p.TotalLength);
        Assert.True(p.HasPorts);
    }

    [Fact]
    public void ParsesIPv4Udp_WithIpOptions()
    {
        var bytes = IPv4(IpProtocol.Udp, [10, 0, 0, 1], [10, 0, 0, 2], 5353, 53, 10, options: 4);
        Assert.True(PacketParser.TryParse(bytes, out var p));
        Assert.Equal(IpProtocol.Udp, p.Protocol);
        Assert.Equal(5353, p.SourcePort);
        Assert.Equal(53, p.DestinationPort);
    }

    [Fact]
    public void ParsesIPv6Tcp_WithHopByHopExtension()
    {
        var src = new byte[16]; src[15] = 1;
        var dst = new byte[16]; dst[0] = 0x20; dst[1] = 0x01; dst[15] = 2;
        var bytes = IPv6(IpProtocol.Tcp, src, dst, 1234, 80, 5, hopByHop: true);
        Assert.True(PacketParser.TryParse(bytes, out var p));
        Assert.True(p.IsIPv6);
        Assert.Equal(IpProtocol.Tcp, p.Protocol);
        Assert.Equal("::1", p.Source.ToString());
        Assert.Equal(1234, p.SourcePort);
        Assert.Equal(80, p.DestinationPort);
        Assert.Equal(bytes.Length, p.TotalLength);
    }

    [Fact]
    public void Icmp_HasNoPorts()
    {
        var bytes = IPv4(1, [10, 0, 0, 1], [10, 0, 0, 2], 0, 0, 8);
        Assert.True(PacketParser.TryParse(bytes, out var p));
        Assert.False(p.HasPorts);
    }

    [Fact]
    public void ConcatenatedPackets_AreSplitByTotalLength()
    {
        var a = IPv4(IpProtocol.Tcp, [10, 0, 0, 1], [10, 0, 0, 2], 1, 2, 3);
        var b = IPv4(IpProtocol.Udp, [10, 0, 0, 3], [10, 0, 0, 4], 5, 6, 7);
        var both = a.Concat(b).ToArray();
        Assert.True(PacketParser.TryParse(both, out var first));
        Assert.Equal(a.Length, first.TotalLength);
        Assert.True(PacketParser.TryParse(both.AsSpan(first.TotalLength), out var second));
        Assert.Equal(IpProtocol.Udp, second.Protocol);
        Assert.Equal(b.Length, second.TotalLength);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x45, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x30, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void Garbage_ReturnsFalse(byte[] bytes)
    {
        Assert.False(PacketParser.TryParse(bytes, out _));
    }

    [Fact]
    public void TotalLengthLargerThanBuffer_ReturnsFalse()
    {
        var bytes = IPv4(IpProtocol.Tcp, [10, 0, 0, 1], [10, 0, 0, 2], 1, 2, 3);
        Assert.False(PacketParser.TryParse(bytes.AsSpan(0, bytes.Length - 1), out _));
    }
}
