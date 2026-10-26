namespace NetworkTrafficController.Core.Exceptions;

/// <summary>WinDivert の失敗理由の分類（公式ドキュメント「WinDivertOpen」のエラーコード表に基づく）。</summary>
public enum WinDivertFailure
{
    /// <summary>分類できない Win32 エラー。</summary>
    Unknown = 0,

    /// <summary>WinDivert.dll が exe の隣に存在しない。</summary>
    LibraryFileNotFound,

    /// <summary>ERROR_FILE_NOT_FOUND (2): WinDivert64.sys が見つからない。</summary>
    DriverFileNotFound,

    /// <summary>
    /// ERROR_NO_SUCH_DEVICE (433): ドライバのデバイスを開けない。
    /// 2026-09-30 に、sc stop WinDivert の後でドライバが停止処理中（STOP_PENDING）のまま固まった状態で実際に発生した。再起動で直る。
    /// </summary>
    DriverDeviceUnavailable,

    /// <summary>
    /// ERROR_SERVICE_DISABLED (1058): WinDivert のサービス登録が無効のまま残っている。
    /// 2026-09-30 に、停止したドライバの登録が片付く直前に起動して実際に発生した。少し待つか再起動すると登録が消えて直る。
    /// </summary>
    DriverServiceDisabled,

    /// <summary>ERROR_ACCESS_DENIED (5): 管理者権限がない。</summary>
    AccessDenied,

    /// <summary>ERROR_INVALID_PARAMETER (87): フィルタ・レイヤー・priority・フラグが不正。</summary>
    InvalidParameter,

    /// <summary>ERROR_INVALID_IMAGE_HASH (577): ドライバに有効な署名がない。</summary>
    InvalidDriverSignature,

    /// <summary>ERROR_DRIVER_FAILED_PRIOR_UNLOAD (654): 互換性のない別バージョンのドライバが読み込み済み。</summary>
    IncompatibleDriverLoaded,

    /// <summary>ERROR_SERVICE_DOES_NOT_EXIST (1060): NO_INSTALL 指定で未インストール。</summary>
    DriverNotInstalled,

    /// <summary>ERROR_DRIVER_BLOCKED (1275): セキュリティソフト等によりドライバがブロックされた。</summary>
    DriverBlocked,

    /// <summary>EPT_S_NOT_REGISTERED (1753): Base Filtering Engine サービスが無効。</summary>
    BaseFilteringEngineDisabled,
}

/// <summary>WinDivert の呼び出しが失敗したときの例外。Win32 エラーコードと分類を保持する。</summary>
public sealed class WinDivertException : Exception
{
    /// <summary>失敗理由の分類。</summary>
    public WinDivertFailure Failure { get; }

    /// <summary>Win32 エラーコード（<c>Marshal.GetLastWin32Error()</c>）。</summary>
    public int Win32Error { get; }

    /// <summary>分類とエラーコードを指定して生成する。</summary>
    public WinDivertException(WinDivertFailure failure, int win32Error, string message)
        : base(message)
    {
        Failure = failure;
        Win32Error = win32Error;
    }

    /// <summary>Win32 エラーコードから分類を決めて生成する。</summary>
    public static WinDivertException FromWin32(string operation, int win32Error)
    {
        var failure = Classify(win32Error);
        return new WinDivertException(failure, win32Error, $"{operation} failed (Win32 error {win32Error}: {Describe(failure)})");
    }

    /// <summary>Win32 エラーコードを分類する。</summary>
    public static WinDivertFailure Classify(int win32Error) => win32Error switch
    {
        2 => WinDivertFailure.DriverFileNotFound,
        433 => WinDivertFailure.DriverDeviceUnavailable,
        1058 => WinDivertFailure.DriverServiceDisabled,
        5 => WinDivertFailure.AccessDenied,
        87 => WinDivertFailure.InvalidParameter,
        577 => WinDivertFailure.InvalidDriverSignature,
        654 => WinDivertFailure.IncompatibleDriverLoaded,
        1060 => WinDivertFailure.DriverNotInstalled,
        1275 => WinDivertFailure.DriverBlocked,
        1753 => WinDivertFailure.BaseFilteringEngineDisabled,
        _ => WinDivertFailure.Unknown,
    };

    /// <summary>待てば直る一時的な失敗か（ドライバの停止処理中・古いサービス登録の片付け中）。</summary>
    public static bool IsTransient(WinDivertFailure failure) =>
        failure is WinDivertFailure.DriverDeviceUnavailable or WinDivertFailure.DriverServiceDisabled;

    /// <summary>分類に対応する日本語の説明と対処。</summary>
    public static string Describe(WinDivertFailure failure) => failure switch
    {
        WinDivertFailure.LibraryFileNotFound => "WinDivert.dll is not in the same folder as the exe",
        WinDivertFailure.DriverFileNotFound => "WinDivert64.sys is not in the same folder as WinDivert.dll",
        WinDivertFailure.DriverServiceDisabled => "A stale WinDivert driver registration remains. Wait a moment and start again. If that does not help, restart the PC",
        WinDivertFailure.DriverDeviceUnavailable => "Cannot open the WinDivert driver. The driver may be stuck while stopping. Restart the PC",
        WinDivertFailure.AccessDenied => "Administrator privileges are missing. Run as administrator",
        WinDivertFailure.InvalidParameter => "The filter string, layer, priority, or flags are invalid",
        WinDivertFailure.InvalidDriverSignature => "The driver signature is invalid (expired signature, test-signing mode, etc.). WinDivert may not be loadable in this environment",
        WinDivertFailure.IncompatibleDriverLoaded => "An incompatible version of the WinDivert driver is already loaded. Restart the PC or try `sc stop WinDivert`",
        WinDivertFailure.DriverNotInstalled => "The WinDivert driver is not installed",
        WinDivertFailure.DriverBlocked => "Loading the driver was blocked (e.g. by security software)",
        WinDivertFailure.BaseFilteringEngineDisabled => "The Base Filtering Engine (BFE) service is stopped. Start it with `sc start BFE`",
        _ => "Unclassified error",
    };
}
