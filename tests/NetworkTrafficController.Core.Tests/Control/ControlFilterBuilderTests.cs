using NetworkTrafficController.Core.Control;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Control;

public class ControlFilterBuilderTests
{
    [Fact]
    public void NoPorts_FallsBackToCatchAll()
    {
        Assert.Equal(ControlFilterBuilder.CatchAll, ControlFilterBuilder.Build([], []));
    }

    [Fact]
    public void TcpPorts_MatchBothSrcAndDst()
    {
        string f = ControlFilterBuilder.Build([8080], []);
        Assert.Equal("(tcp and (tcp.SrcPort == 8080 or tcp.DstPort == 8080))", f);
    }

    [Fact]
    public void TcpAndUdp_AreJoinedWithOr()
    {
        string f = ControlFilterBuilder.Build([80, 443], [53]);
        Assert.Contains("(tcp and (tcp.SrcPort == 80 or tcp.DstPort == 80 or tcp.SrcPort == 443 or tcp.DstPort == 443))", f);
        Assert.Contains("(udp and (udp.SrcPort == 53 or udp.DstPort == 53))", f);
        Assert.Contains(") or (", f);
    }
}
