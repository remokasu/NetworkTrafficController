using Microsoft.Extensions.Logging.Abstractions;
using NetworkTrafficController.Core.Mapping;
using NetworkTrafficController.Core.Monitoring;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Monitoring;

public class PacketClassifierTests
{
    private static readonly NetAddress Local = NetAddress.FromIPv4Bytes([192, 168, 1, 10]);
    private static readonly NetAddress Remote = NetAddress.FromIPv4Bytes([1, 1, 1, 1]);
    private static readonly NetAddress Lo = NetAddress.FromIPv4Bytes([127, 0, 0, 1]);

    private static (FlowTable Table, MonitorAggregator Agg) Create()
    {
        var time = new FakeTimeProvider();
        return (new FlowTable(new FlowTableOptions(), time, NullLogger.Instance), new MonitorAggregator(new MonitorAggregatorOptions(), time));
    }

    private static ParsedPacket Packet(NetAddress src, ushort srcPort, NetAddress dst, ushort dstPort, int length) =>
        new(false, IpProtocol.Tcp, src, srcPort, dst, dstPort, length);

    [Fact]
    public void Outbound_ResolvesWithSourceAsLocal()
    {
        var (table, agg) = Create();
        table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Local, 50000, Remote, 443), 100, 0);

        PacketClassifier.Classify(Packet(Local, 50000, Remote, 443, 1000), new PacketMeta(0, Outbound: true, Loopback: false, IsIPv6: false), table, agg);

        var s = agg.Snapshot();
        var p = Assert.Single(s.Processes);
        Assert.Equal(100u, p.Pid);
        Assert.Equal(1000, p.Counters.OutBytes);
        Assert.Equal(0, p.Counters.InBytes);
        Assert.Equal(1000, s.Total.OutBytes);
        Assert.Equal(0, s.UnclassifiedTcp.OutPackets);
    }

    [Fact]
    public void Inbound_ResolvesWithDestinationAsLocal()
    {
        var (table, agg) = Create();
        table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Local, 50000, Remote, 443), 100, 0);

        PacketClassifier.Classify(Packet(Remote, 443, Local, 50000, 500), new PacketMeta(0, Outbound: false, Loopback: false, IsIPv6: false), table, agg);

        var p = Assert.Single(agg.Snapshot().Processes);
        Assert.Equal(500, p.Counters.InBytes);
        Assert.Equal(1, p.Counters.InPackets);
    }

    [Fact]
    public void Unresolved_CountsAsUnclassified_NotAttributed()
    {
        var (table, agg) = Create();
        PacketClassifier.Classify(Packet(Local, 1, Remote, 2, 70), new PacketMeta(0, true, false, false), table, agg);

        var s = agg.Snapshot();
        Assert.Empty(s.Processes);
        Assert.Equal(70, s.UnclassifiedTcp.OutBytes);
        Assert.Equal(70, s.Total.OutBytes);
    }

    [Fact]
    public void Loopback_AttributesToSenderAndReceiver_ButTotalOnce()
    {
        var (table, agg) = Create();
        table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Lo, 40000, Lo, 8080), 11, 0); // client
        table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Lo, 8080, Lo, 40000), 22, 0); // server

        PacketClassifier.Classify(Packet(Lo, 40000, Lo, 8080, 300), new PacketMeta(0, Outbound: true, Loopback: true, IsIPv6: false), table, agg);

        var s = agg.Snapshot();
        Assert.Equal(2, s.Processes.Count);
        var client = s.Processes.Single(p => p.Pid == 11);
        var server = s.Processes.Single(p => p.Pid == 22);
        Assert.Equal(300, client.Counters.OutBytes);
        Assert.Equal(0, client.Counters.InBytes);
        Assert.Equal(300, server.Counters.InBytes);
        Assert.Equal(0, server.Counters.OutBytes);
        Assert.Equal(300, s.Total.OutBytes);
        Assert.Equal(1, s.Total.OutPackets);
        Assert.Equal(1, s.LoopbackPackets);
        Assert.All(s.Connections, c => Assert.True(c.Loopback));
    }

    [Fact]
    public void Loopback_OnlyOneSideKnown_AttributesThatSideOnly()
    {
        var (table, agg) = Create();
        table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Lo, 8080, Lo, 40000), 22, 0); // server only

        PacketClassifier.Classify(Packet(Lo, 40000, Lo, 8080, 300), new PacketMeta(0, true, true, false), table, agg);

        var p = Assert.Single(agg.Snapshot().Processes);
        Assert.Equal(22u, p.Pid);
        Assert.Equal(300, p.Counters.InBytes);
    }

    [Fact]
    public void NonTcpUdp_CountsAsOtherProtocol()
    {
        var (table, agg) = Create();
        var icmp = new ParsedPacket(false, 1, Local, 0, Remote, 0, 64);
        PacketClassifier.Classify(icmp, new PacketMeta(0, true, false, false), table, agg);

        var s = agg.Snapshot();
        Assert.Equal(1, s.OtherProtocolPackets);
        Assert.Equal(64, s.OtherProtocolBytes);
        Assert.Equal(64, s.Total.OutBytes);
    }
}
