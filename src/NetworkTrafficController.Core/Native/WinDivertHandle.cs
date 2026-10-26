using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NetworkTrafficController.Core.Exceptions;

namespace NetworkTrafficController.Core.Native;

/// <summary>
/// WinDivert ハンドルの薄いラッパー。開く・受信・送信・パラメータ・Shutdown・Close を提供する。
/// Dispose は必ず <c>Shutdown(Both)</c> → <c>Close</c> の順で行う（ADR-0004）。
/// </summary>
internal sealed unsafe class WinDivertHandle : IDisposable
{
    private const long InsufficientBufferLogLimit = 10;
    private readonly WinDivertSafeHandle _handle;
    private readonly ILogger _logger;
    private long _insufficientBufferCount;
    private int _disposed;

    private WinDivertHandle(WinDivertSafeHandle handle, string filter, WinDivertLayer layer, short priority, WinDivertFlags flags, ILogger logger)
    {
        _handle = handle;
        _logger = logger;
        Filter = filter;
        Layer = layer;
        Priority = priority;
        Flags = flags;
    }

    /// <summary>開いたときのフィルタ文字列。</summary>
    public string Filter { get; }

    /// <summary>レイヤー。</summary>
    public WinDivertLayer Layer { get; }

    /// <summary>priority。</summary>
    public short Priority { get; }

    /// <summary>フラグ。</summary>
    public WinDivertFlags Flags { get; }

    /// <summary>ハンドルが有効か（Dispose 前）。</summary>
    public bool IsOpen => _disposed == 0 && !_handle.IsInvalid && !_handle.IsClosed;

    /// <summary>ERROR_INSUFFICIENT_BUFFER で捨てたバッチ数（観測可能性）。</summary>
    public long InsufficientBufferCount => Interlocked.Read(ref _insufficientBufferCount);

    /// <summary>ハンドルを開く。失敗時は Win32 エラーを分類した <see cref="WinDivertException"/> を投げる。</summary>
    public static WinDivertHandle Open(string filter, WinDivertLayer layer, short priority, WinDivertFlags flags, ILogger logger)
    {
        NativeLibraryLoader.EnsureRegistered();
        NativeLibraryLoader.EnsureNativeFilesPresent();

        var handle = WinDivertNative.WinDivertOpen(filter, layer, priority, flags);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.SetHandleAsInvalid();
            throw WinDivertException.FromWin32($"WinDivertOpen(layer={layer}, priority={priority}, flags={flags}, filter=\"{filter}\")", error);
        }

        var wrapper = new WinDivertHandle(handle, filter, layer, priority, flags, logger);
        HandleRegistry.Register(wrapper);
        logger.LogInformation("WinDivert handle opened: layer={Layer} priority={Priority} flags={Flags} filter=\"{Filter}\"", layer, priority, flags, filter);
        return wrapper;
    }

    /// <summary>ドライバのバージョンを取得する。</summary>
    public (ulong Major, ulong Minor) GetDriverVersion()
    {
        return (GetParam(WinDivertParam.VersionMajor), GetParam(WinDivertParam.VersionMinor));
    }

    /// <summary>パラメータを取得する。</summary>
    public ulong GetParam(WinDivertParam param)
    {
        ulong value;
        if (!WinDivertNative.WinDivertGetParam(_handle, param, &value))
        {
            throw WinDivertException.FromWin32($"WinDivertGetParam({param})", Marshal.GetLastPInvokeError());
        }

        return value;
    }

    /// <summary>パラメータを設定する。</summary>
    public void SetParam(WinDivertParam param, ulong value)
    {
        if (!WinDivertNative.WinDivertSetParam(_handle, param, value))
        {
            throw WinDivertException.FromWin32($"WinDivertSetParam({param}, {value})", Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>
    /// パケット（またはイベント）をまとめて受信する。ブロッキング。
    /// 戻り値が false のときは Shutdown 済みでもう受信できない（<c>ERROR_NO_DATA</c>）。
    /// それ以外の失敗は例外。
    /// </summary>
    /// <param name="packetBuffer">NETWORK レイヤーではパケットの連結先。FLOW レイヤーでは空でよい。</param>
    /// <param name="addresses">受信したアドレスの書き込み先。長さが受信できる最大件数。</param>
    /// <param name="bytesReceived">packetBuffer に書かれたバイト数。</param>
    /// <param name="addressCount">受信したアドレス数。</param>
    public bool TryReceive(Span<byte> packetBuffer, Span<WinDivertAddress> addresses, out uint bytesReceived, out int addressCount)
    {
        bytesReceived = 0;
        addressCount = 0;
        if (addresses.IsEmpty)
        {
            throw new ArgumentException("addresses must not be empty", nameof(addresses));
        }

        uint recvLen = 0;
        uint addrLen = (uint)(addresses.Length * WinDivertAddress.Size);
        bool ok;
        fixed (byte* pPacket = packetBuffer)
        fixed (WinDivertAddress* pAddr = addresses)
        {
            ok = WinDivertNative.WinDivertRecvEx(
                _handle,
                packetBuffer.IsEmpty ? null : pPacket,
                (uint)packetBuffer.Length,
                &recvLen,
                0,
                pAddr,
                &addrLen,
                nint.Zero);
        }

        if (!ok)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == WinDivertConstants.ErrorNoData)
            {
                return false;
            }

            if (error == WinDivertConstants.ErrorInsufficientBuffer)
            {
                // 失敗時に pRecvLen / pAddrLen が更新される保証はバッチ版のドキュメントに無い（ADR-0002 未確認項目）。
                // ネイティブが返した長さを信用せず「このバッチは 0 件」として継続する。
                Interlocked.Increment(ref _insufficientBufferCount);
                if (_insufficientBufferCount <= InsufficientBufferLogLimit)
                {
                    _logger.LogWarning("WinDivertRecvEx: ERROR_INSUFFICIENT_BUFFER (buffer {Len} bytes); batch discarded (count={Count})", packetBuffer.Length, _insufficientBufferCount);
                }

                return true;
            }

            throw WinDivertException.FromWin32("WinDivertRecvEx", error);
        }

        // ネイティブ境界の値は必ず managed 側の容量で clamp する
        bytesReceived = Math.Min(recvLen, (uint)packetBuffer.Length);
        addressCount = Math.Min((int)(addrLen / WinDivertAddress.Size), addresses.Length);
        return true;
    }

    /// <summary>パケットを注入する（制御ハンドル用、フェーズ 3 以降で使用）。</summary>
    public void Send(ReadOnlySpan<byte> packet, in WinDivertAddress address)
    {
        uint sendLen;
        bool ok;
        fixed (byte* pPacket = packet)
        fixed (WinDivertAddress* pAddr = &address)
        {
            ok = WinDivertNative.WinDivertSendEx(_handle, pPacket, (uint)packet.Length, &sendLen, 0, pAddr, WinDivertAddress.Size, nint.Zero);
        }

        if (!ok)
        {
            throw WinDivertException.FromWin32("WinDivertSendEx", Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>受信 / 送信を停止する。受信スレッドは次の TryReceive で false を返して抜ける。</summary>
    public void Shutdown(WinDivertShutdownMode how)
    {
        if (_handle.IsInvalid || _handle.IsClosed)
        {
            return;
        }

        if (!WinDivertNative.WinDivertShutdown(_handle, how))
        {
            _logger.LogWarning("WinDivertShutdown({How}) failed: Win32 error {Error}", how, Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>Shutdown(Both) してから閉じる。多重呼び出し可。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            Shutdown(WinDivertShutdownMode.Both);
        }
        finally
        {
            _handle.Dispose();
            HandleRegistry.Unregister(this);
            _logger.LogInformation("WinDivert handle closed: layer={Layer} priority={Priority}", Layer, Priority);
        }
    }
}
