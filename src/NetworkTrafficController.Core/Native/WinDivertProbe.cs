using Microsoft.Extensions.Logging;
using NetworkTrafficController.Core.Exceptions;

namespace NetworkTrafficController.Core.Native;

/// <summary>ドライバ読み込み検証の結果。</summary>
/// <param name="Success">ハンドルを開けたか。</param>
/// <param name="DriverMajor">ドライバのメジャーバージョン。</param>
/// <param name="DriverMinor">ドライバのマイナーバージョン。</param>
/// <param name="Failure">失敗理由の分類。</param>
/// <param name="Win32Error">Win32 エラーコード。</param>
/// <param name="Message">説明。</param>
public sealed record ProbeResult(bool Success, ulong DriverMajor, ulong DriverMinor, WinDivertFailure Failure, int Win32Error, string Message);

/// <summary>
/// WinDivert ドライバが本機で読み込めるかを確認する（ADR-0000 Q1）。
/// 何も捕まえないフィルタ <c>false</c> の SNIFF ハンドルを開いて即閉じるので、通信には影響しない。
/// </summary>
public static class WinDivertProbe
{
    /// <summary>検証を実行する。</summary>
    public static ProbeResult Run(ILogger logger)
    {
        try
        {
            using var handle = WinDivertHandle.Open("false", WinDivertLayer.Network, 0, WinDivertFlags.Sniff | WinDivertFlags.RecvOnly, logger);
            var (major, minor) = handle.GetDriverVersion();
            return new ProbeResult(true, major, minor, WinDivertFailure.Unknown, 0, $"WinDivert driver {major}.{minor} loaded successfully");
        }
        catch (WinDivertException ex)
        {
            logger.LogError(ex, "WinDivert probe failed");
            return new ProbeResult(false, 0, 0, ex.Failure, ex.Win32Error, ex.Message);
        }
    }
}
