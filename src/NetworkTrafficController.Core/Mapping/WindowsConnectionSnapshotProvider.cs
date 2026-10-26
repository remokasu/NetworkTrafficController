using NetworkTrafficController.Core.Exceptions;
using NetworkTrafficController.Core.Native;

namespace NetworkTrafficController.Core.Mapping;

/// <summary>GetExtendedTcpTable / GetExtendedUdpTable で既存接続を取得する（ADR-0001 (3)）。</summary>
public sealed unsafe class WindowsConnectionSnapshotProvider : IConnectionSnapshotProvider
{
    private const int InitialBufferSize = 64 * 1024;
    private const int MaxRetries = 4;

    /// <inheritdoc />
    public IReadOnlyList<TcpConnectionRow> GetTcpConnections()
    {
        var rows = new List<TcpConnectionRow>(256);
        var v4 = Fetch(static (p, s, af) => IpHelperNative.GetExtendedTcpTable(p, s, false, af, IpHelperNative.TcpTableOwnerPidAll, 0), IpHelperNative.AfInet, "GetExtendedTcpTable(AF_INET)");
        IpHelperTables.ParseTcp4(v4, rows);
        var v6 = Fetch(static (p, s, af) => IpHelperNative.GetExtendedTcpTable(p, s, false, af, IpHelperNative.TcpTableOwnerPidAll, 0), IpHelperNative.AfInet6, "GetExtendedTcpTable(AF_INET6)");
        IpHelperTables.ParseTcp6(v6, rows);
        return rows;
    }

    /// <inheritdoc />
    public IReadOnlyList<UdpEndpointRow> GetUdpEndpoints()
    {
        var rows = new List<UdpEndpointRow>(256);
        var v4 = Fetch(static (p, s, af) => IpHelperNative.GetExtendedUdpTable(p, s, false, af, IpHelperNative.UdpTableOwnerPid, 0), IpHelperNative.AfInet, "GetExtendedUdpTable(AF_INET)");
        IpHelperTables.ParseUdp4(v4, rows);
        var v6 = Fetch(static (p, s, af) => IpHelperNative.GetExtendedUdpTable(p, s, false, af, IpHelperNative.UdpTableOwnerPid, 0), IpHelperNative.AfInet6, "GetExtendedUdpTable(AF_INET6)");
        IpHelperTables.ParseUdp6(v6, rows);
        return rows;
    }

    private delegate uint TableCall(byte* buffer, uint* size, uint af);

    private static byte[] Fetch(TableCall call, uint af, string apiName)
    {
        var buffer = new byte[InitialBufferSize];
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            uint size = (uint)buffer.Length;
            uint result;
            fixed (byte* p = buffer)
            {
                result = call(p, &size, af);
            }

            if (result == IpHelperNative.NoError)
            {
                return buffer;
            }

            if (result != IpHelperNative.ErrorInsufficientBuffer)
            {
                throw new Win32CallException(apiName, (int)result);
            }

            buffer = new byte[Math.Max(size, (uint)buffer.Length * 2)];
        }

        throw new Win32CallException(apiName, (int)IpHelperNative.ErrorInsufficientBuffer);
    }
}
