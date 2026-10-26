using System.Buffers.Binary;
using NetworkTrafficController.Core.Mapping;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Mapping;

public class IpHelperTablesTests
{
    [Theory]
    [InlineData(0x0000BB01u, 443)]   // bytes 01 BB → port 443
    [InlineData(0x00005000u, 80)]    // bytes 00 50 → port 80
    [InlineData(0x0000FFFFu, 65535)]
    [InlineData(0u, 0)]
    public void PortFromDword_ConvertsLow16BitsFromNetworkOrder(uint dword, int expected)
    {
        Assert.Equal((ushort)expected, IpHelperTables.PortFromDword(dword));
    }

    [Fact]
    public void ParseTcp4_ReadsRowFields()
    {
        var table = new byte[4 + IpHelperTables.Tcp4RowSize];
        BinaryPrimitives.WriteUInt32LittleEndian(table, 1);
        var row = table.AsSpan(4);
        BinaryPrimitives.WriteUInt32LittleEndian(row, (uint)TcpState.Established);
        BinaryPrimitives.WriteUInt32LittleEndian(row[4..], 0x0100007F);   // 127.0.0.1
        BinaryPrimitives.WriteUInt32LittleEndian(row[8..], 0x0000BB01);   // 443
        BinaryPrimitives.WriteUInt32LittleEndian(row[12..], 0x0302000A);  // 10.0.2.3
        BinaryPrimitives.WriteUInt32LittleEndian(row[16..], 0x00005000);  // 80
        BinaryPrimitives.WriteUInt32LittleEndian(row[20..], 4242);

        var rows = new List<TcpConnectionRow>();
        IpHelperTables.ParseTcp4(table, rows);

        var r = Assert.Single(rows);
        Assert.Equal(TcpState.Established, r.State);
        Assert.Equal(4242u, r.Pid);
        Assert.Equal(IpProtocol.Tcp, r.Key.Protocol);
        Assert.Equal("127.0.0.1", r.Key.LocalAddress.ToString());
        Assert.Equal(443, r.Key.LocalPort);
        Assert.Equal("10.0.2.3", r.Key.RemoteAddress.ToString());
        Assert.Equal(80, r.Key.RemotePort);
    }

    [Fact]
    public void ParseTcp6_ReadsRowFields()
    {
        var table = new byte[4 + IpHelperTables.Tcp6RowSize];
        BinaryPrimitives.WriteUInt32LittleEndian(table, 1);
        var row = table.AsSpan(4);
        row[15] = 1; // ::1 local
        BinaryPrimitives.WriteUInt32LittleEndian(row[20..], 0x0000BB01);
        row[24] = 0x20; row[25] = 0x01; row[26] = 0x0d; row[27] = 0xb8; row[39] = 2; // 2001:db8::2 remote
        BinaryPrimitives.WriteUInt32LittleEndian(row[44..], 0x00005000);
        BinaryPrimitives.WriteUInt32LittleEndian(row[48..], (uint)TcpState.Listen);
        BinaryPrimitives.WriteUInt32LittleEndian(row[52..], 7);

        var rows = new List<TcpConnectionRow>();
        IpHelperTables.ParseTcp6(table, rows);

        var r = Assert.Single(rows);
        Assert.Equal(TcpState.Listen, r.State);
        Assert.Equal(7u, r.Pid);
        Assert.Equal("::1", r.Key.LocalAddress.ToString());
        Assert.Equal(443, r.Key.LocalPort);
        Assert.Equal("2001:db8::2", r.Key.RemoteAddress.ToString());
        Assert.Equal(80, r.Key.RemotePort);
    }

    [Fact]
    public void ParseUdp4_And_Udp6_ReadRows()
    {
        var t4 = new byte[4 + IpHelperTables.Udp4RowSize * 2];
        BinaryPrimitives.WriteUInt32LittleEndian(t4, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(t4.AsSpan(4), 0);            // 0.0.0.0
        BinaryPrimitives.WriteUInt32LittleEndian(t4.AsSpan(8), 0x00003500);   // 53
        BinaryPrimitives.WriteUInt32LittleEndian(t4.AsSpan(12), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(t4.AsSpan(16), 0x0100007F);
        BinaryPrimitives.WriteUInt32LittleEndian(t4.AsSpan(20), 0x0000D204);  // 1234
        BinaryPrimitives.WriteUInt32LittleEndian(t4.AsSpan(24), 200);

        var rows = new List<UdpEndpointRow>();
        IpHelperTables.ParseUdp4(t4, rows);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].Key.Address.IsUnspecified);
        Assert.Equal(53, rows[0].Key.Port);
        Assert.Equal(100u, rows[0].Pid);
        Assert.Equal("127.0.0.1", rows[1].Key.Address.ToString());
        Assert.Equal(1234, rows[1].Key.Port);

        var t6 = new byte[4 + IpHelperTables.Udp6RowSize];
        BinaryPrimitives.WriteUInt32LittleEndian(t6, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(t6.AsSpan(4 + 20), 0x00003500);
        BinaryPrimitives.WriteUInt32LittleEndian(t6.AsSpan(4 + 24), 300);
        rows.Clear();
        IpHelperTables.ParseUdp6(t6, rows);
        var r6 = Assert.Single(rows);
        Assert.True(r6.Key.Address.IsUnspecified);
        Assert.Equal(53, r6.Key.Port);
        Assert.Equal(300u, r6.Pid);
    }

    [Fact]
    public void Parse_EmptyTable_ProducesNoRows()
    {
        var rows = new List<TcpConnectionRow>();
        IpHelperTables.ParseTcp4(new byte[4], rows);
        Assert.Empty(rows);
    }
}
