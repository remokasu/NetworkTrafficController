using System.Reflection;
using System.Runtime.InteropServices;
using NetworkTrafficController.Core.Exceptions;

namespace NetworkTrafficController.Core.Native;

/// <summary>
/// WinDivert.dll を exe と同じフォルダ（<see cref="AppContext.BaseDirectory"/>）の絶対パスで読み込む。
/// カレントディレクトリや PATH からは読み込まない（管理者権限プロセスの DLL 差し替え対策、ADR-0004）。
/// リゾルバは <see cref="WinDivertNative"/> の静的コンストラクタで登録するため、どの関数を先に呼んでも有効になる。
/// </summary>
public static class NativeLibraryLoader
{
    private const string DriverFileName = "WinDivert64.sys";
    private static int _registered;

    /// <summary>WinDivert.dll の期待パス。</summary>
    public static string LibraryPath => Path.Combine(AppContext.BaseDirectory, WinDivertNative.LibraryName);

    /// <summary>WinDivert64.sys の期待パス。DLL は自分と同じフォルダからドライバを探す。</summary>
    public static string DriverPath => Path.Combine(AppContext.BaseDirectory, DriverFileName);

    /// <summary>DLL リゾルバを登録する（複数回呼んでも 1 回だけ登録される）。</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryLoader).Assembly, Resolve);
    }

    /// <summary>DLL と .sys が exe の隣に存在するかを、ドライバを読み込む前に検査する。</summary>
    /// <exception cref="WinDivertException">どちらかが存在しない場合。</exception>
    public static void EnsureNativeFilesPresent()
    {
        if (!File.Exists(LibraryPath))
        {
            throw new WinDivertException(WinDivertFailure.LibraryFileNotFound, 0, $"WinDivert.dll not found: {LibraryPath}");
        }

        if (!File.Exists(DriverPath))
        {
            throw new WinDivertException(WinDivertFailure.DriverFileNotFound, 2, $"WinDivert64.sys not found: {DriverPath}");
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, WinDivertNative.LibraryName, StringComparison.OrdinalIgnoreCase))
        {
            // それ以外（iphlpapi / kernel32）は AssemblyInfo の DefaultDllImportSearchPaths(System32) に従う
            return nint.Zero;
        }

        EnsureNativeFilesPresent();
        return NativeLibrary.Load(LibraryPath);
    }
}
