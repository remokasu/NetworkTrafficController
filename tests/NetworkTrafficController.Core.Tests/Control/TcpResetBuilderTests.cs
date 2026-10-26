using System.Buffers.Binary;
using NetworkTrafficController.Core.Control;
using NetworkTrafficController.Core.Mapping;
using NetworkTrafficController.Core.Monitoring;
using NetworkTrafficController.Core.Tests.Monitoring;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Control;

public class TcpResetBuilderTests
{
    private static byte[] Tcp4(byte[] src, byte[] dst, ushort srcPort, ushort dstPort, uint seq, uint ack, byte flags, int payload)
    {
        var p = PacketParserTests.IPv4(IpProtocol.Tcp, src, dst, srcPort, dstPort, payload);
        var tcp = p.AsSpan(20);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[8..], ack);
        tcp[12] = 0x50;
        tcp[13] = flags;
        return p;
    }

    private const byte Ack = 0x10;
    private const byte Syn = 0x02;
    private const byte Rst = 0x04;

    [Fact]
    public void Build_SwapsAddressesAndPorts()
    {
        var original = Tcp4([10, 0, 0, 1], [10, 0, 0, 2], 50000, 443, seq: 1000, ack: 2000, flags: Ack, payload: 0);
        Assert.True(TcpResetBuilder.TryBuild(original, out var reset));
        Assert.True(PacketParser.TryParse(reset, out var parsed));

        Assert.Equal(IpProtocol.Tcp, parsed.Protocol);
        Assert.Equal("10.0.0.2", parsed.Source.ToString());   // 送信元 = 元の宛先
        Assert.Equal("10.0.0.1", parsed.Destination.ToString());
        Assert.Equal(443, parsed.SourcePort);
        Assert.Equal(50000, parsed.DestinationPort);
        Assert.Equal(40, reset.Length);
    }

    [Fact]
    public void Build_SetsRstAndAck_WithSeqFromOriginalAck()
    {
        var original = Tcp4([10, 0, 0, 1], [10, 0, 0, 2], 1, 2, seq: 1000, ack: 2000, flags: Ack, payload: 50);
        Assert.True(TcpResetBuilder.TryBuild(original, out var reset));

        var tcp = reset.AsSpan(20);
        Assert.Equal(2000u, BinaryPrimitives.ReadUInt32BigEndian(tcp[4..]));         // RST.Seq = 元の Ack
        Assert.Equal(1050u, BinaryPrimitives.ReadUInt32BigEndian(tcp[8..]));         // RST.Ack = 元 Seq + payload
        Assert.Equal(Rst | Ack, tcp[13] & (Rst | Ack));
    }

    [Fact]
    public void Build_NonAckPacket_SeqIsZero_AckCountsSyn()
    {
        var original = Tcp4([10, 0, 0, 1], [10, 0, 0, 2], 1, 2, seq: 500, ack: 0, flags: Syn, payload: 0);
        Assert.True(TcpResetBuilder.TryBuild(original, out var reset));

        var tcp = reset.AsSpan(20);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(tcp[4..]));    // ACK フラグ無し → Seq 0
        Assert.Equal(501u, BinaryPrimitives.ReadUInt32BigEndian(tcp[8..]));  // SYN は 1 を足す
    }

    [Fact]
    public void Build_RejectsRstPacket()
    {
        var original = Tcp4([10, 0, 0, 1], [10, 0, 0, 2], 1, 2, seq: 1, ack: 1, flags: Rst | Ack, payload: 0);
        Assert.False(TcpResetBuilder.TryBuild(original, out _));
    }

    [Fact]
    public void Build_RejectsUdpAndGarbage()
    {
        var udp = PacketParserTests.IPv4(IpProtocol.Udp, [10, 0, 0, 1], [10, 0, 0, 2], 1, 2, 8);
        Assert.False(TcpResetBuilder.TryBuild(udp, out _));
        Assert.False(TcpResetBuilder.TryBuild([0xFF, 0x00], out _));
        Assert.False(TcpResetBuilder.TryBuild([], out _));
    }

    [Fact]
    public void Build_RejectsIPv4Fragment()
    {
        var p = Tcp4([10, 0, 0, 1], [10, 0, 0, 2], 1, 2, seq: 1, ack: 1, flags: Ack, payload: 0);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(6), 100); // fragment offset != 0
        Assert.False(TcpResetBuilder.TryBuild(p, out _));
    }

    [Fact]
    public void Build_AcceptsIPv4WithOptions()
    {
        // IHL=6（24 バイトヘッダ、オプション 4 バイト）
        var p = PacketParserTests.IPv4(IpProtocol.Tcp, [10, 0, 0, 1], [10, 0, 0, 2], 50000, 443, payload: 0, options: 4);
        var tcp = p.AsSpan(24);
        tcp[12] = 0x50;
        tcp[13] = Ack;
        Assert.True(TcpResetBuilder.TryBuild(p, out var reset));
        Assert.True(PacketParser.TryParse(reset, out var parsed));
        Assert.Equal("10.0.0.2", parsed.Source.ToString());
        Assert.Equal(443, parsed.SourcePort);
    }

    [Fact]
    public void Build_RejectsIPv6WithExtensionHeader()
    {
        var src = new byte[16]; src[15] = 1;
        var dst = new byte[16]; dst[15] = 2;
        var p = PacketParserTests.IPv6(IpProtocol.Tcp, src, dst, 40000, 80, payload: 0, hopByHop: true);
        Assert.False(TcpResetBuilder.TryBuild(p, out _)); // 拡張ヘッダ付きは扱わない → 無音破棄にフォールバック
    }

    [Fact]
    public void Build_RejectsTruncatedTcpHeader()
    {
        var p = Tcp4([10, 0, 0, 1], [10, 0, 0, 2], 1, 2, seq: 1, ack: 1, flags: Ack, payload: 0);
        Assert.False(TcpResetBuilder.TryBuild(p.AsSpan(0, 34).ToArray(), out _)); // TCP ヘッダ 20 バイトに満たない
    }

    [Fact]
    public void Build_IPv6_SwapsAddresses()
    {
        var src = new byte[16]; src[15] = 1;
        var dst = new byte[16]; dst[0] = 0x20; dst[1] = 0x01; dst[2] = 0x0d; dst[3] = 0xb8; dst[15] = 2;
        var p = PacketParserTests.IPv6(IpProtocol.Tcp, src, dst, 40000, 80, payload: 0);
        var tcp = p.AsSpan(40);
        tcp[12] = 0x50;
        tcp[13] = Ack;

        Assert.True(TcpResetBuilder.TryBuild(p, out var reset));
        Assert.True(PacketParser.TryParse(reset, out var parsed));
        Assert.True(parsed.IsIPv6);
        Assert.Equal("2001:db8::2", parsed.Source.ToString());
        Assert.Equal("::1", parsed.Destination.ToString());
        Assert.Equal(80, parsed.SourcePort);
        Assert.Equal(40000, parsed.DestinationPort);
        Assert.Equal(60, reset.Length);
    }
}
