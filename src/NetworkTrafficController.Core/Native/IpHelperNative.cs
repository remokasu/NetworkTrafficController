using System.Runtime.InteropServices;

namespace NetworkTrafficController.Core.Native;

/// <summary>iphlpapi.dll の P/Invoke 宣言（GetExtendedTcpTable / GetExtendedUdpTable）。</summary>
internal static unsafe partial class IpHelperNative
{
    private const string LibraryName = "iphlpapi.dll";

    public const uint AfInet = 2;
    public const uint AfInet6 = 23;

    /// <summary>TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL。</summary>
    public const uint TcpTableOwnerPidAll = 5;

    /// <summary>UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID。</summary>
    public const uint UdpTableOwnerPid = 1;

    public const uint NoError = 0;
    public const uint ErrorInsufficientBuffer = 122;

    [LibraryImport(LibraryName, EntryPoint = "GetExtendedTcpTable")]
    public static partial uint GetExtendedTcpTable(byte* pTcpTable, uint* pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, uint ulAf, uint tableClass, uint reserved);

    [LibraryImport(LibraryName, EntryPoint = "GetExtendedUdpTable")]
    public static partial uint GetExtendedUdpTable(byte* pUdpTable, uint* pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, uint ulAf, uint tableClass, uint reserved);
}
