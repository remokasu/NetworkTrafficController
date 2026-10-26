using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetworkTrafficController.Core.Native;

/// <summary>kernel32.dll のプロセス情報 API（OpenProcess / QueryFullProcessImageNameW / GetProcessTimes）。</summary>
internal static unsafe partial class ProcessNative
{
    private const string LibraryName = "kernel32.dll";

    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION。保護プロセスにも許可される最小権限。</summary>
    public const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport(LibraryName, EntryPoint = "OpenProcess", SetLastError = true)]
    public static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport(LibraryName, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* exeName, uint* size);

    [LibraryImport(LibraryName, EntryPoint = "GetProcessTimes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(SafeProcessHandle process, long* creationTime, long* exitTime, long* kernelTime, long* userTime);
}
