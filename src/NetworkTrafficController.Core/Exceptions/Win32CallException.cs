using System.Runtime.InteropServices;

namespace NetworkTrafficController.Core.Exceptions;

/// <summary>iphlpapi / kernel32 等の Win32 API 呼び出しが失敗したときの例外。</summary>
public sealed class Win32CallException : Exception
{
    /// <summary>失敗した API 名。</summary>
    public string Api { get; }

    /// <summary>Win32 エラーコード（戻り値または <c>GetLastError</c>）。</summary>
    public int Win32Error { get; }

    /// <summary>API 名とエラーコードを指定して生成する。</summary>
    public Win32CallException(string api, int win32Error)
        : base($"{api} failed (Win32 error {win32Error}: {Marshal.GetPInvokeErrorMessage(win32Error)})")
    {
        Api = api;
        Win32Error = win32Error;
    }
}
