using NetworkTrafficController.Core.Mapping;

namespace NetworkTrafficController.Core.Monitoring;

/// <summary>FLOW レイヤーのイベント（WinDivert 依存を外した表現）。</summary>
/// <param name="Established">true = FLOW_ESTABLISHED、false = FLOW_DELETED。</param>
/// <param name="Key">5-tuple（local / remote は WinDivert の Flow データのまま）。</param>
/// <param name="Pid">プロセス ID。</param>
/// <param name="EndpointId">WinDivert の EndpointId。</param>
/// <param name="Outbound">フローの方向（接続を開始した側なら true）。</param>
/// <param name="Loopback">loopback フローか。</param>
/// <param name="RawLocalWords">デバッグ用: LocalAddr の UINT32[4] 生値（バイトオーダー検証）。</param>
/// <param name="RawLocalPort">デバッグ用: LocalPort の生値。</param>
public readonly record struct FlowEvent(
    bool Established,
    FlowKey Key,
    uint Pid,
    ulong EndpointId,
    bool Outbound,
    bool Loopback,
    (uint W0, uint W1, uint W2, uint W3) RawLocalWords,
    ushort RawLocalPort);

/// <summary>FLOW イベントの供給元。<see cref="TryReceive"/> はブロッキングで、Shutdown 後は false を返す。</summary>
public interface IFlowEventSource : IDisposable
{
    /// <summary>次のイベントを待って受け取る。供給元が閉じられたら false。</summary>
    bool TryReceive(out FlowEvent flowEvent);

    /// <summary>受信を停止させる（受信スレッドを抜けさせる）。</summary>
    void Shutdown();
}

/// <summary>受信したパケット 1 個の付加情報（WinDivert の WINDIVERT_ADDRESS から必要な分だけ抜き出したもの）。</summary>
/// <param name="Timestamp">WinDivert のタイムスタンプ（QPC）。</param>
/// <param name="Outbound">送信方向か。</param>
/// <param name="Loopback">loopback パケットか。</param>
/// <param name="IsIPv6">IPv6 か。</param>
public readonly record struct PacketMeta(long Timestamp, bool Outbound, bool Loopback, bool IsIPv6);

/// <summary>NETWORK レイヤーのパケット供給元（監視 = SNIFF）。バッチ受信。Shutdown 後は false を返す。</summary>
public interface IPacketSource : IDisposable
{
    /// <summary>
    /// パケットをまとめて受け取る。<paramref name="buffer"/> に連結して書かれ、
    /// <paramref name="metas"/> の先頭 <paramref name="packetCount"/> 件が各パケットに対応する。
    /// パケット境界は IP ヘッダの長さで求める（<see cref="PacketParser"/>）。
    /// </summary>
    bool TryReceiveBatch(Span<byte> buffer, Span<PacketMeta> metas, out int bytesReceived, out int packetCount);

    /// <summary>受信を停止させる。</summary>
    void Shutdown();
}
