using System.Buffers.Binary;
using System.Net;

namespace NetworkTrafficController.Core.Mapping;

/// <summary>
/// 16 バイト（IPv6 表現）に正規化した IP アドレス。IPv4 は IPv4-mapped IPv6（<c>::ffff:a.b.c.d</c>）で持つ。
/// 照合表のキーとして使うため、割り当てのない値型で比較可能にしている（ADR-0001）。
/// </summary>
public readonly record struct NetAddress
{
    private readonly ulong _hi;
    private readonly ulong _lo;

    private NetAddress(ulong hi, ulong lo)
    {
        _hi = hi;
        _lo = lo;
    }

    /// <summary>ネットワークバイトオーダーの 16 バイトから生成する（パケットヘッダ / MIB_TCP6ROW 用）。</summary>
    public static NetAddress FromBytes(ReadOnlySpan<byte> bytes16)
    {
        if (bytes16.Length < 16)
        {
            throw new ArgumentException("16 bytes required", nameof(bytes16));
        }

        return new NetAddress(BinaryPrimitives.ReadUInt64BigEndian(bytes16), BinaryPrimitives.ReadUInt64BigEndian(bytes16[8..]));
    }

    /// <summary>ネットワークバイトオーダーの 4 バイトから IPv4-mapped で生成する（IPv4 パケットヘッダ用）。</summary>
    public static NetAddress FromIPv4Bytes(ReadOnlySpan<byte> bytes4)
    {
        if (bytes4.Length < 4)
        {
            throw new ArgumentException("4 bytes required", nameof(bytes4));
        }

        ulong lo = 0x0000_FFFF_0000_0000UL | BinaryPrimitives.ReadUInt32BigEndian(bytes4);
        return new NetAddress(0, lo);
    }

    /// <summary>
    /// メモリ上の <c>in_addr</c>（ネットワークバイトオーダーの 4 バイト）を little-endian の uint として読んだ値から生成する
    /// （<c>MIB_TCPROW_OWNER_PID.dwLocalAddr</c> 等）。
    /// </summary>
    public static NetAddress FromIPv4RawUInt32(uint inAddrAsStored)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, inAddrAsStored);
        return FromIPv4Bytes(b);
    }

    /// <summary>
    /// WinDivert の <c>UINT32[4]</c> 表現から生成する。WinDivert のヘルパー（<c>WinDivertHelperNtohIPv6Address</c>）は
    /// 「要素 [0] が最下位 32 ビット、各要素はホストバイトオーダー」の形式を使い、FLOW レイヤーの
    /// <c>LocalAddr</c> / <c>RemoteAddr</c> もこの形式で届く。IPv4-mapped では <c>[0]</c> が IPv4 アドレス（ホストオーダー）、
    /// <c>[1] == 0x0000FFFF</c> になる。この前提はフェーズ 2 の実機検証（--flow-dump）で確認する。
    /// </summary>
    public static NetAddress FromWinDivertWords(ReadOnlySpan<uint> words4)
    {
        if (words4.Length < 4)
        {
            throw new ArgumentException("4 words required", nameof(words4));
        }

        ulong hi = ((ulong)words4[3] << 32) | words4[2];
        ulong lo = ((ulong)words4[1] << 32) | words4[0];
        return new NetAddress(hi, lo);
    }

    /// <summary><see cref="IPAddress"/> から生成する。</summary>
    public static NetAddress FromIPAddress(IPAddress address)
    {
        var mapped = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? address.MapToIPv6() : address;
        Span<byte> b = stackalloc byte[16];
        if (!mapped.TryWriteBytes(b, out _))
        {
            throw new ArgumentException("unsupported address", nameof(address));
        }

        return FromBytes(b);
    }

    /// <summary>IPv4-mapped IPv6 か。</summary>
    public bool IsIPv4Mapped => _hi == 0 && (_lo >> 32) == 0x0000_FFFF;

    /// <summary>未指定アドレス（0.0.0.0 / ::）か。ワイルドカード待受の判定に使う。</summary>
    public bool IsUnspecified => (_hi == 0 && _lo == 0) || (IsIPv4Mapped && (uint)_lo == 0);

    /// <summary>loopback アドレス（127.0.0.0/8 / ::1）か。表示上の補助であり、判定は WinDivert の Loopback フラグを正とする。</summary>
    public bool IsLoopback => IsIPv4Mapped ? ((_lo >> 24) & 0xFF) == 127 : (_hi == 0 && _lo == 1);

    /// <summary>IPv4-mapped の場合の IPv4 アドレス（ホストバイトオーダー）。</summary>
    public uint IPv4HostOrder => (uint)_lo;

    /// <summary>16 バイト（ネットワークバイトオーダー）に書き出す。</summary>
    public void WriteBytes(Span<byte> destination16)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination16, _hi);
        BinaryPrimitives.WriteUInt64BigEndian(destination16[8..], _lo);
    }

    /// <summary><see cref="IPAddress"/> に変換する（表示用。IPv4-mapped は IPv4 に戻す）。</summary>
    public IPAddress ToIPAddress()
    {
        Span<byte> b = stackalloc byte[16];
        WriteBytes(b);
        var v6 = new IPAddress(b);
        return IsIPv4Mapped ? v6.MapToIPv4() : v6;
    }

    /// <inheritdoc />
    public override string ToString() => ToIPAddress().ToString();
}
