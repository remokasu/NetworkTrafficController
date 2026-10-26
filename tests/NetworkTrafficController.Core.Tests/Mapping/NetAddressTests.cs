using System.Net;
using NetworkTrafficController.Core.Mapping;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Mapping;

public class NetAddressTests
{
    [Fact]
    public void FromIPv4Bytes_ProducesIPv4Mapped_AndRoundTrips()
    {
        var a = NetAddress.FromIPv4Bytes([127, 0, 0, 1]);
        Assert.True(a.IsIPv4Mapped);
        Assert.True(a.IsLoopback);
        Assert.False(a.IsUnspecified);
        Assert.Equal("127.0.0.1", a.ToString());
        Assert.Equal(0x7F000001u, a.IPv4HostOrder);
    }

    [Fact]
    public void FromIPv4RawUInt32_TreatsValueAsInAddrInMemory()
    {
        // in_addr for 127.0.0.1 is bytes 7F 00 00 01; read little-endian as uint = 0x0100007F
        var a = NetAddress.FromIPv4RawUInt32(0x0100007F);
        Assert.Equal("127.0.0.1", a.ToString());
    }

    [Fact]
    public void FromWinDivertWords_UsesWordZeroAsIPv4HostOrder()
    {
        var a = NetAddress.FromWinDivertWords([0x7F000001, 0x0000FFFF, 0, 0]);
        Assert.True(a.IsIPv4Mapped);
        Assert.Equal("127.0.0.1", a.ToString());
    }

    [Fact]
    public void FromWinDivertWords_IPv6Loopback()
    {
        var a = NetAddress.FromWinDivertWords([1, 0, 0, 0]);
        Assert.False(a.IsIPv4Mapped);
        Assert.True(a.IsLoopback);
        Assert.Equal("::1", a.ToString());
    }

    [Fact]
    public void FromBytes_IPv6_MatchesIPAddress()
    {
        var ip = IPAddress.Parse("2001:db8::1");
        var a = NetAddress.FromIPAddress(ip);
        Span<byte> b = stackalloc byte[16];
        a.WriteBytes(b);
        Assert.Equal(ip.GetAddressBytes(), b.ToArray());
        Assert.Equal(ip, a.ToIPAddress());
    }

    [Theory]
    [InlineData("0.0.0.0", true)]
    [InlineData("::", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData("::1", false)]
    public void IsUnspecified(string text, bool expected)
    {
        Assert.Equal(expected, NetAddress.FromIPAddress(IPAddress.Parse(text)).IsUnspecified);
    }

    [Fact]
    public void Equality_IsByValue()
    {
        var a = NetAddress.FromIPv4Bytes([10, 1, 2, 3]);
        var b = NetAddress.FromIPAddress(IPAddress.Parse("10.1.2.3"));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
