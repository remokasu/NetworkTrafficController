using System.Buffers.Binary;

namespace NetworkTrafficController.Core.Control;

/// <summary>
/// 捕まえた TCP パケットから、その送信元へ返す RST パケットを組み立てる（ADR-0005 §3、公式サンプル netfilter.c の方式）。
/// アドレスとポートを入れ替え、SEQ = 元パケットの ACK 番号（ACK フラグが無ければ 0）、
/// ACK = 元パケットの SEQ + ペイロード長 + SYN + FIN とし、RST と ACK のフラグを立てる。
/// チェックサムは 0 のまま返す（注入時に <c>WinDivertHelperCalcChecksums</c> で計算する）。
/// 注入の方向（WinDivert アドレスの Outbound）は呼び出し側が元パケットと逆にする。
/// </summary>
public static class TcpResetBuilder
{
    private const int IPv4HeaderLength = 20;
    private const int IPv6HeaderLength = 40;
    private const int TcpHeaderLength = 20;
    private const byte TcpProtocol = 6;
    private const byte DefaultTtl = 64;

    private const byte FlagFin = 0x01;
    private const byte FlagSyn = 0x02;
    private const byte FlagRst = 0x04;
    private const byte FlagAck = 0x10;

    /// <summary>RST パケットを組み立てる。TCP でない・元が RST・壊れている・IPv6 拡張ヘッダ付きの場合は false。</summary>
    public static bool TryBuild(ReadOnlySpan<byte> original, out byte[] reset)
    {
        reset = [];
        if (original.Length < 1)
        {
            return false;
        }

        return (original[0] >> 4) switch
        {
            4 => TryBuildIPv4(original, out reset),
            6 => TryBuildIPv6(original, out reset),
            _ => false,
        };
    }

    private static bool TryBuildIPv4(ReadOnlySpan<byte> p, out byte[] reset)
    {
        reset = [];
        if (p.Length < IPv4HeaderLength)
        {
            return false;
        }

        int ihl = (p[0] & 0x0F) * 4;
        int totalLength = BinaryPrimitives.ReadUInt16BigEndian(p[2..]);
        int fragmentOffset = BinaryPrimitives.ReadUInt16BigEndian(p[6..]) & 0x1FFF;
        if (p[9] != TcpProtocol || ihl < IPv4HeaderLength || totalLength > p.Length || totalLength < ihl + TcpHeaderLength || fragmentOffset != 0)
        {
            return false;
        }

        if (!TryReadTcp(p[ihl..totalLength], out var tcp))
        {
            return false;
        }

        reset = new byte[IPv4HeaderLength + TcpHeaderLength];
        var ip = reset.AsSpan();
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)reset.Length);
        ip[8] = DefaultTtl;
        ip[9] = TcpProtocol;
        p.Slice(16, 4).CopyTo(ip[12..]); // 送信元 = 元の宛先
        p.Slice(12, 4).CopyTo(ip[16..]); // 宛先 = 元の送信元
        WriteTcp(ip[IPv4HeaderLength..], tcp);
        return true;
    }

    private static bool TryBuildIPv6(ReadOnlySpan<byte> p, out byte[] reset)
    {
        reset = [];
        if (p.Length < IPv6HeaderLength || p[6] != TcpProtocol)
        {
            return false; // 拡張ヘッダ付きは扱わない（呼び出し側は無音の破棄にフォールバックする）
        }

        int totalLength = IPv6HeaderLength + BinaryPrimitives.ReadUInt16BigEndian(p[4..]);
        if (totalLength > p.Length || totalLength < IPv6HeaderLength + TcpHeaderLength)
        {
            return false;
        }

        if (!TryReadTcp(p[IPv6HeaderLength..totalLength], out var tcp))
        {
            return false;
        }

        reset = new byte[IPv6HeaderLength + TcpHeaderLength];
        var ip = reset.AsSpan();
        ip[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(ip[4..], TcpHeaderLength);
        ip[6] = TcpProtocol;
        ip[7] = DefaultTtl;
        p.Slice(24, 16).CopyTo(ip[8..]);  // 送信元 = 元の宛先
        p.Slice(8, 16).CopyTo(ip[24..]);  // 宛先 = 元の送信元
        WriteTcp(ip[IPv6HeaderLength..], tcp);
        return true;
    }

    private readonly record struct TcpFields(ushort SrcPort, ushort DstPort, uint ResetSeq, uint ResetAck);

    private static bool TryReadTcp(ReadOnlySpan<byte> segment, out TcpFields fields)
    {
        fields = default;
        if (segment.Length < TcpHeaderLength)
        {
            return false;
        }

        int dataOffset = (segment[12] >> 4) * 4;
        byte flags = segment[13];
        if (dataOffset < TcpHeaderLength || dataOffset > segment.Length || (flags & FlagRst) != 0)
        {
            return false; // RST に RST は返さない
        }

        uint seq = BinaryPrimitives.ReadUInt32BigEndian(segment[4..]);
        uint ack = BinaryPrimitives.ReadUInt32BigEndian(segment[8..]);
        uint payload = (uint)(segment.Length - dataOffset);
        uint resetSeq = (flags & FlagAck) != 0 ? ack : 0;
        uint resetAck = unchecked(seq + payload + ((flags & FlagSyn) != 0 ? 1u : 0u) + ((flags & FlagFin) != 0 ? 1u : 0u));
        fields = new TcpFields(
            BinaryPrimitives.ReadUInt16BigEndian(segment),
            BinaryPrimitives.ReadUInt16BigEndian(segment[2..]),
            resetSeq,
            resetAck);
        return true;
    }

    private static void WriteTcp(Span<byte> tcp, in TcpFields original)
    {
        BinaryPrimitives.WriteUInt16BigEndian(tcp, original.DstPort);    // 送信元ポート = 元の宛先ポート
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], original.SrcPort); // 宛先ポート = 元の送信元ポート
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], original.ResetSeq);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[8..], original.ResetAck);
        tcp[12] = (TcpHeaderLength / 4) << 4;
        tcp[13] = FlagRst | FlagAck;
    }
}
