using System.Runtime.InteropServices;

namespace NetworkTrafficController.Core.Native;

/// <summary>WinDivert のレイヤー（<c>WINDIVERT_LAYER</c>）。</summary>
internal enum WinDivertLayer : uint
{
    Network = 0,
    NetworkForward = 1,
    Flow = 2,
    Socket = 3,
    Reflect = 4,
}

/// <summary>WinDivert のイベント種別（<c>WINDIVERT_EVENT</c>）。</summary>
internal enum WinDivertEvent : byte
{
    NetworkPacket = 0,
    FlowEstablished = 1,
    FlowDeleted = 2,
    SocketBind = 3,
    SocketConnect = 4,
    SocketListen = 5,
    SocketAccept = 6,
    SocketClose = 7,
    ReflectOpen = 8,
    ReflectClose = 9,
}

/// <summary>WinDivertOpen のフラグ（<c>WINDIVERT_FLAG_*</c>）。</summary>
[Flags]
internal enum WinDivertFlags : ulong
{
    None = 0,
    Sniff = 0x0001,
    Drop = 0x0002,
    RecvOnly = 0x0004,
    SendOnly = 0x0008,
    NoInstall = 0x0010,
    Fragments = 0x0020,
}

/// <summary>WinDivertSetParam / GetParam のパラメータ（<c>WINDIVERT_PARAM</c>）。</summary>
internal enum WinDivertParam : uint
{
    QueueLength = 0,
    QueueTime = 1,
    QueueSize = 2,
    VersionMajor = 3,
    VersionMinor = 4,
}

/// <summary>WinDivertShutdown の対象（<c>WINDIVERT_SHUTDOWN</c>）。</summary>
internal enum WinDivertShutdownMode : uint
{
    Recv = 0x1,
    Send = 0x2,
    Both = 0x3,
}

/// <summary>WinDivert の定数（windivert.h 2.2.2 から転記）。</summary>
internal static class WinDivertConstants
{
    public const short PriorityHighest = 30000;
    public const short PriorityLowest = -30000;
    public const ulong QueueLengthDefault = 4096;
    public const ulong QueueLengthMax = 16384;
    public const ulong QueueTimeDefault = 2000;
    public const ulong QueueSizeDefault = 4194304;
    public const ulong QueueSizeMax = 33554432;
    public const int BatchMax = 0xFF;
    /// <summary>1 パケットの最大長（40 + 0xFFFF）。</summary>
    public const int MtuMax = 40 + 0xFFFF;
    /// <summary>WinDivertRecv 系が「もうデータは来ない」を表す Win32 エラー（ERROR_NO_DATA）。</summary>
    public const int ErrorNoData = 232;
    /// <summary>ERROR_INSUFFICIENT_BUFFER。</summary>
    public const int ErrorInsufficientBuffer = 122;
}

/// <summary>NETWORK レイヤーの付加情報（<c>WINDIVERT_DATA_NETWORK</c>）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WinDivertDataNetwork
{
    public uint IfIdx;
    public uint SubIfIdx;
}

/// <summary>FLOW レイヤーの付加情報（<c>WINDIVERT_DATA_FLOW</c>、64 バイトにパディングされる）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WinDivertDataFlow
{
    public ulong EndpointId;
    public ulong ParentEndpointId;
    public uint ProcessId;
    public fixed uint LocalAddr[4];
    public fixed uint RemoteAddr[4];
    public ushort LocalPort;
    public ushort RemotePort;
    public byte Protocol;
}

/// <summary>
/// <c>WINDIVERT_ADDRESS</c>（80 バイト）。先頭 8 バイトが Timestamp、次の 4 バイトがビットフィールド、
/// 4 バイトの Reserved2、残り 64 バイトがレイヤー別データの union。
/// MSVC のビットフィールドは宣言順に最下位ビットから詰められる。
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = Size)]
internal struct WinDivertAddress
{
    public const int Size = 80;
    public const int UnionOffset = 16;

    [FieldOffset(0)] public long Timestamp;
    [FieldOffset(8)] public uint Bits;
    [FieldOffset(12)] public uint Reserved2;
    [FieldOffset(UnionOffset)] public WinDivertDataNetwork Network;
    [FieldOffset(UnionOffset)] public WinDivertDataFlow Flow;

    public WinDivertLayer Layer => (WinDivertLayer)(Bits & 0xFF);
    public WinDivertEvent Event => (WinDivertEvent)((Bits >> 8) & 0xFF);
    public bool Sniffed => (Bits & (1u << 16)) != 0;
    public bool Outbound
    {
        get => (Bits & (1u << 17)) != 0;
        set => Bits = value ? Bits | (1u << 17) : Bits & ~(1u << 17);
    }
    public bool Loopback => (Bits & (1u << 18)) != 0;
    public bool Impostor => (Bits & (1u << 19)) != 0;
    public bool IPv6 => (Bits & (1u << 20)) != 0;
    public bool IPChecksum => (Bits & (1u << 21)) != 0;
    public bool TcpChecksum => (Bits & (1u << 22)) != 0;
    public bool UdpChecksum => (Bits & (1u << 23)) != 0;
}

/// <summary>
/// WinDivert.dll の P/Invoke 宣言。ここ以外で WinDivert を直接呼ばない（CLAUDE.md）。
/// win-x64 のみ対象。DLL は <see cref="NativeLibraryLoader"/> が exe と同じフォルダから絶対パスで読み込む。
/// </summary>
internal static unsafe partial class WinDivertNative
{
    public const string LibraryName = "WinDivert.dll";

    /// <summary>どの関数が最初に呼ばれても、絶対パスのリゾルバが先に登録されるようにする（呼び出し順への依存をなくす）。</summary>
    static WinDivertNative() => NativeLibraryLoader.EnsureRegistered();

    [LibraryImport(LibraryName, EntryPoint = "WinDivertOpen", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial WinDivertSafeHandle WinDivertOpen(string filter, WinDivertLayer layer, short priority, WinDivertFlags flags);

    [LibraryImport(LibraryName, EntryPoint = "WinDivertRecvEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertRecvEx(
        WinDivertSafeHandle handle,
        byte* pPacket,
        uint packetLen,
        uint* pRecvLen,
        ulong flags,
        WinDivertAddress* pAddr,
        uint* pAddrLen,
        nint lpOverlapped);

    [LibraryImport(LibraryName, EntryPoint = "WinDivertSendEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertSendEx(
        WinDivertSafeHandle handle,
        byte* pPacket,
        uint packetLen,
        uint* pSendLen,
        ulong flags,
        WinDivertAddress* pAddr,
        uint addrLen,
        nint lpOverlapped);

    [LibraryImport(LibraryName, EntryPoint = "WinDivertShutdown", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertShutdown(WinDivertSafeHandle handle, WinDivertShutdownMode how);

    /// <summary>生ハンドルで閉じる。<see cref="WinDivertSafeHandle.ReleaseHandle"/> 専用。</summary>
    [LibraryImport(LibraryName, EntryPoint = "WinDivertClose", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertClose(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "WinDivertSetParam", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertSetParam(WinDivertSafeHandle handle, WinDivertParam param, ulong value);

    [LibraryImport(LibraryName, EntryPoint = "WinDivertGetParam", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertGetParam(WinDivertSafeHandle handle, WinDivertParam param, ulong* pValue);

    [LibraryImport(LibraryName, EntryPoint = "WinDivertHelperCalcChecksums", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertHelperCalcChecksums(byte* pPacket, uint packetLen, WinDivertAddress* pAddr, ulong flags);

    [LibraryImport(LibraryName, EntryPoint = "WinDivertHelperCompileFilter", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertHelperCompileFilter(string filter, WinDivertLayer layer, byte* obj, uint objLen, byte** errorStr, uint* errorPos);
}
