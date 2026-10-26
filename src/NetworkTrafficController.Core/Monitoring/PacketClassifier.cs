using NetworkTrafficController.Core.Mapping;

namespace NetworkTrafficController.Core.Monitoring;

/// <summary>
/// 解析済みパケットを照合表で PID に帰属させ、<see cref="MonitorAggregator"/> に計上する純粋なロジック。
/// 通常パケットは方向で local / remote を決めて 1 回照合し、loopback は送信側・受信側で 2 回照合する（ADR-0001 / ADR-0003）。
/// </summary>
public static class PacketClassifier
{
    /// <summary>1 パケットを集計へ反映する。</summary>
    public static void Classify(in ParsedPacket packet, in PacketMeta meta, FlowTable table, MonitorAggregator aggregator)
    {
        var direction = meta.Outbound ? Direction.Outbound : Direction.Inbound;
        aggregator.RecordPacket(direction, packet.TotalLength, meta.Loopback);

        if (!packet.HasPorts)
        {
            aggregator.RecordOtherProtocol(packet.TotalLength);
            return;
        }

        if (meta.Loopback)
        {
            ClassifyLoopback(packet, table, aggregator);
            return;
        }

        var key = direction == Direction.Outbound
            ? new FlowKey(packet.Protocol, packet.Source, packet.SourcePort, packet.Destination, packet.DestinationPort)
            : new FlowKey(packet.Protocol, packet.Destination, packet.DestinationPort, packet.Source, packet.SourcePort);

        if (table.TryResolve(key, out uint pid, out _))
        {
            aggregator.Attribute(pid, key, direction, packet.TotalLength, loopback: false);
        }
        else
        {
            aggregator.RecordUnclassified(packet.Protocol, direction, packet.TotalLength);
        }
    }

    private static void ClassifyLoopback(in ParsedPacket packet, FlowTable table, MonitorAggregator aggregator)
    {
        var senderKey = new FlowKey(packet.Protocol, packet.Source, packet.SourcePort, packet.Destination, packet.DestinationPort);
        var receiverKey = senderKey.Reversed;
        bool any = false;

        if (table.TryResolve(senderKey, out uint senderPid, out _))
        {
            aggregator.Attribute(senderPid, senderKey, Direction.Outbound, packet.TotalLength, loopback: true);
            any = true;
        }

        if (table.TryResolve(receiverKey, out uint receiverPid, out _))
        {
            aggregator.Attribute(receiverPid, receiverKey, Direction.Inbound, packet.TotalLength, loopback: true);
            any = true;
        }

        if (!any)
        {
            aggregator.RecordUnclassified(packet.Protocol, Direction.Outbound, packet.TotalLength);
        }
    }
}
