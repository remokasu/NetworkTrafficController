using System.Buffers;
using Microsoft.Extensions.Logging;
using NetworkTrafficController.Core.Monitoring;
using NetworkTrafficController.Core.Native;

namespace NetworkTrafficController.Core.Control;

/// <summary>
/// 制御ハンドルから受け取った 1 パケット。バイト列と、再注入に必要な付加情報（WinDivert アドレス）を保持する。
/// 遅延キューに載せている間もバイト列を保持するため、各パケットは専用のバッファに複製される。
/// </summary>
internal abstract class ControlPacketToken
{
    /// <summary>パケットのバイト列。</summary>
    public abstract ReadOnlyMemory<byte> Data { get; }

    /// <summary>方向・loopback などの付加情報。</summary>
    public abstract PacketMeta Meta { get; }
}

/// <summary>制御ハンドルの入出力の抽象（テストでフェイクに差し替える）。</summary>
internal interface IControlChannel : IDisposable
{
    /// <summary>1 パケットを受け取る。ブロッキング。Shutdown 後にキューが空になったら false。</summary>
    bool TryReceive(out ControlPacketToken token);

    /// <summary>パケットを再注入する（チェックサム再計算を含む）。バッファは返却される。</summary>
    void Send(ControlPacketToken token);

    /// <summary>パケットを破棄する（再注入せずバッファだけ返却する）。</summary>
    void Release(ControlPacketToken token);

    /// <summary>
    /// <paramref name="original"/> の付加情報（インターフェース等）を基に、方向（Outbound）を反転して
    /// <paramref name="packet"/> を注入する（TCP RST 用）。<paramref name="original"/> のバッファは返却しない。
    /// </summary>
    /// <returns>注入（<c>WinDivertSend</c>）に成功したか。</returns>
    bool InjectReverse(ControlPacketToken original, ReadOnlySpan<byte> packet);

    /// <summary>受信を停止させる（受信スレッドを抜けさせる）。</summary>
    void Shutdown();
}

/// <summary>WinDivert の通常モード NETWORK ハンドルで実装した制御チャネル（ADR-0002 制御ハンドル）。</summary>
internal sealed unsafe class WinDivertControlChannel : IControlChannel
{
    /// <summary>制御ハンドルの priority（ADR-0002: 監視 -1000 より高い 0）。</summary>
    public const short Priority = 0;

    private const int ReceiveBufferSize = WinDivertConstants.MtuMax;
    private const ulong NoChecksumOffloadFlags = 0; // 全チェックサムを再計算（loopback 対策、ADR-0003）

    private sealed class Token : ControlPacketToken
    {
        public byte[] Buffer = [];
        public int Length;
        public WinDivertAddress Address;
        public override ReadOnlyMemory<byte> Data => Buffer.AsMemory(0, Length);
        public override PacketMeta Meta => new(Address.Timestamp, Address.Outbound, Address.Loopback, Address.IPv6);
    }

    private readonly WinDivertHandle _handle;
    private readonly ILogger _logger;

    // 制御ハンドルは 1 回に 1 パケットだけ受信する。バッチ受信だとパケット境界を IP ヘッダから辿る必要があり、
    // 解析できないパケットの境界が分からなくなって再注入できず通信を落とす（通常モードでは受信 = 横取り）。
    // 1 パケットずつなら recvLen が正確な長さなので、解析に失敗してもバイト列をそのまま再注入できる（fail-open）。
    private readonly byte[] _receiveBuffer = new byte[ReceiveBufferSize];
    private readonly WinDivertAddress[] _receiveAddress = new WinDivertAddress[1];

    private WinDivertControlChannel(WinDivertHandle handle, ILogger logger)
    {
        _handle = handle;
        _logger = logger;
    }

    /// <summary>制御ハンドルを指定フィルタで開く（フィルタは <see cref="ControlFilterBuilder"/> で作る）。</summary>
    public static WinDivertControlChannel Open(string filter, ILogger logger)
    {
        var handle = WinDivertHandle.Open(filter, WinDivertLayer.Network, Priority, WinDivertFlags.None, logger);
        return new WinDivertControlChannel(handle, logger);
    }

    /// <inheritdoc />
    public bool TryReceive(out ControlPacketToken token)
    {
        while (true)
        {
            if (!_handle.TryReceive(_receiveBuffer, _receiveAddress, out uint bytes, out int count))
            {
                token = null!;
                return false; // Shutdown 済みでキューが空
            }

            if (count == 0 || bytes == 0)
            {
                continue; // バッファ不足で捨てたバッチ等。次を待つ
            }

            int len = (int)bytes;
            var buffer = ArrayPool<byte>.Shared.Rent(len);
            _receiveBuffer.AsSpan(0, len).CopyTo(buffer);
            token = new Token { Buffer = buffer, Length = len, Address = _receiveAddress[0] };
            return true;
        }
    }

    /// <inheritdoc />
    public void Send(ControlPacketToken token)
    {
        var t = (Token)token;
        try
        {
            // 再注入前にチェックサムを再計算（loopback のチェックサムオフロード対策、ADR-0003）
            bool checksummed;
            fixed (byte* p = t.Buffer)
            fixed (WinDivertAddress* a = &t.Address)
            {
                checksummed = WinDivertNative.WinDivertHelperCalcChecksums(p, (uint)t.Length, a, NoChecksumOffloadFlags);
            }

            if (!checksummed)
            {
                // 再注入は続ける（fail-open）。頻発しうるので Debug
                _logger.LogDebug("WinDivertHelperCalcChecksums failed (len={Len}, Win32 {Error})", t.Length, System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
            }

            _handle.Send(t.Buffer.AsSpan(0, t.Length), t.Address);
        }
        catch (Exception ex)
        {
            // 再注入の失敗で受信ループを止めない（fail-open。1 パケット落ちるだけ）
            _logger.LogWarning(ex, "control reinject failed (len={Len})", t.Length);
        }
        finally
        {
            Return(t);
        }
    }

    /// <inheritdoc />
    public void Release(ControlPacketToken token) => Return((Token)token);

    /// <inheritdoc />
    public bool InjectReverse(ControlPacketToken original, ReadOnlySpan<byte> packet)
    {
        var address = ((Token)original).Address;

        // 方向を反転して注入する（送信を捕まえたら受信経路へ = ローカルアプリへ RST を届ける。netfilter 方式）。
        // 注: loopback では、方向を反転してもしなくても RST は相手に届かず、結果として無音のタイムアウトになる
        //（WinDivert は loopback を送信専用として扱う仕様。ADR-0003 U5 で実機確認）。loopback の即時切断は Block を使う。
        address.Outbound = !address.Outbound;

        var buffer = ArrayPool<byte>.Shared.Rent(packet.Length);
        try
        {
            packet.CopyTo(buffer);
            bool checksummed;
            fixed (byte* p = buffer)
            {
                checksummed = WinDivertNative.WinDivertHelperCalcChecksums(p, (uint)packet.Length, &address, NoChecksumOffloadFlags);
            }

            if (!checksummed)
            {
                _logger.LogDebug("WinDivertHelperCalcChecksums failed for injected packet (len={Len}, Win32 {Error})", packet.Length, System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
            }

            _handle.Send(buffer.AsSpan(0, packet.Length), address);
            return true;
        }
        catch (Exception ex)
        {
            // RST の注入に失敗しても受信ループは止めない（元パケットは破棄済みなので、結果は無音の破棄と同じ）
            _logger.LogWarning(ex, "reverse inject failed (len={Len})", packet.Length);
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void Return(Token token)
    {
        if (token.Buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(token.Buffer);
            token.Buffer = [];
        }
    }

    /// <inheritdoc />
    public void Shutdown() => _handle.Shutdown(WinDivertShutdownMode.Recv);

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();
}
