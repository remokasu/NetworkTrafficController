using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NetworkTrafficController.Core.Native;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Native;

public class WinDivertAddressLayoutTests
{
    [Fact]
    public void Address_Is80Bytes_MatchingWinDivertHeader()
    {
        Assert.Equal(80, Marshal.SizeOf<WinDivertAddress>());
        Assert.Equal(80, Unsafe.SizeOf<WinDivertAddress>());
    }

    [Fact]
    public void FlowData_Is64Bytes_FillingTheUnion()
    {
        Assert.Equal(64, Marshal.SizeOf<WinDivertDataFlow>());
    }

    [Fact]
    public void Bits_DecodeLayerEventAndFlags_FromLeastSignificantBit()
    {
        var addr = new WinDivertAddress
        {
            Bits = (uint)WinDivertLayer.Flow | ((uint)WinDivertEvent.FlowEstablished << 8) | (1u << 16) | (1u << 17) | (1u << 18),
        };

        Assert.Equal(WinDivertLayer.Flow, addr.Layer);
        Assert.Equal(WinDivertEvent.FlowEstablished, addr.Event);
        Assert.True(addr.Sniffed);
        Assert.True(addr.Outbound);
        Assert.True(addr.Loopback);
        Assert.False(addr.IPv6);
    }

    [Fact]
    public void Outbound_Setter_TogglesOnlyItsBit()
    {
        var addr = new WinDivertAddress { Bits = 0xFFFF_FFFF };
        addr.Outbound = false;
        Assert.Equal(0xFFFF_FFFF & ~(1u << 17), addr.Bits);
        addr.Outbound = true;
        Assert.Equal(0xFFFF_FFFF, addr.Bits);
    }

    [Fact]
    public unsafe void FlowData_FieldOffsets_MatchCLayout()
    {
        var flow = default(WinDivertDataFlow);
        byte* basePtr = (byte*)&flow;
        Assert.Equal(0, (byte*)&flow.EndpointId - basePtr);
        Assert.Equal(8, (byte*)&flow.ParentEndpointId - basePtr);
        Assert.Equal(16, (byte*)&flow.ProcessId - basePtr);
        Assert.Equal(20, (byte*)flow.LocalAddr - basePtr);
        Assert.Equal(36, (byte*)flow.RemoteAddr - basePtr);
        Assert.Equal(52, (byte*)&flow.LocalPort - basePtr);
        Assert.Equal(54, (byte*)&flow.RemotePort - basePtr);
        Assert.Equal(56, (byte*)&flow.Protocol - basePtr);
    }
}
