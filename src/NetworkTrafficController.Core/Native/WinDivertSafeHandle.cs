using Microsoft.Win32.SafeHandles;

namespace NetworkTrafficController.Core.Native;

/// <summary>
/// WinDivert ハンドルの SafeHandle。<c>WinDivertOpen</c> は失敗時に <c>INVALID_HANDLE_VALUE</c> を返す。
/// 受信スレッドが <c>WinDivertRecvEx</c> でブロック中は SafeHandle の参照カウントにより解放が保留されるため、
/// 閉じる前に必ず <c>WinDivertShutdown</c> を呼ぶこと（<see cref="WinDivertHandle"/> が担当）。
/// </summary>
internal sealed class WinDivertSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>LibraryImport の戻り値マーシャリング用（引数なしコンストラクタが必須）。</summary>
    public WinDivertSafeHandle()
        : base(ownsHandle: true)
    {
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle() => WinDivertNative.WinDivertClose(handle);
}
