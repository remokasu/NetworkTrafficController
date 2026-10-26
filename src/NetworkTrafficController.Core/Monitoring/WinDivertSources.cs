using Microsoft.Extensions.Logging;
using NetworkTrafficController.Core.Mapping;
using NetworkTrafficController.Core.Native;

namespace NetworkTrafficController.Core.Monitoring;

/// <summary>監視に使う 2 つのハンドル（FLOW と SNIFF）をまとめて開く。</summary>
public static class MonitorSources
{
    /// <summary>
    /// FLOW → SNIFF の順に開く（ADR-0001 の起動順）。SNIFF で失敗したら、先に開いた FLOW を閉じてから例外を投げ直す。
    /// 起動をやり直すたびにハンドルが残って増えないようにするため。
    /// </summary>
    public static (WinDivertFlowSource Flow, WinDivertSniffSource Sniff) Open(ILoggerFactory loggerFactory)
    {
        var flow = WinDivertFlowSource.Open(loggerFactory.CreateLogger<WinDivertFlowSource>());
        try
        {
            return (flow, WinDivertSniffSource.Open(loggerFactory.CreateLogger<WinDivertSniffSource>()));
        }
        catch
        {
            flow.Dispose();
            throw;
        }
    }
}

/// <summary>FLOW レイヤーのハンドル（SNIFF | RECV_ONLY、priority 0）を <see cref="IFlowEventSource"/> として公開する。</summary>
public sealed class WinDivertFlowSource : IFlowEventSource
{
    /// <summary>FLOW ハンドルの priority（ADR-0002）。</summary>
    public const short Priority = 0;

    private const string Filter = "tcp or udp";
    private readonly WinDivertHandle _handle;
    private readonly WinDivertAddress[] _addresses = new WinDivertAddress[1];

    private WinDivertFlowSource(WinDivertHandle handle)
    {
        _handle = handle;
    }

    /// <summary>FLOW ハンドルを開く。</summary>
    public static WinDivertFlowSource Open(ILogger logger)
    {
        var handle = WinDivertHandle.Open(Filter, WinDivertLayer.Flow, Priority, WinDivertFlags.Sniff | WinDivertFlags.RecvOnly, logger);
        return new WinDivertFlowSource(handle);
    }

    /// <inheritdoc />
    public unsafe bool TryReceive(out FlowEvent flowEvent)
    {
        flowEvent = default;
        if (!_handle.TryReceive(Span<byte>.Empty, _addresses, out _, out int count) || count == 0)
        {
            return false;
        }

        ref var addr = ref _addresses[0];
        var flow = addr.Flow;
        var local = NetAddress.FromWinDivertWords(new ReadOnlySpan<uint>(flow.LocalAddr, 4));
        var remote = NetAddress.FromWinDivertWords(new ReadOnlySpan<uint>(flow.RemoteAddr, 4));
        var key = new FlowKey(flow.Protocol, local, flow.LocalPort, remote, flow.RemotePort);
        flowEvent = new FlowEvent(
            addr.Event == WinDivertEvent.FlowEstablished,
            key,
            flow.ProcessId,
            flow.EndpointId,
            addr.Outbound,
            addr.Loopback,
            (flow.LocalAddr[0], flow.LocalAddr[1], flow.LocalAddr[2], flow.LocalAddr[3]),
            flow.LocalPort);
        return true;
    }

    /// <inheritdoc />
    public void Shutdown() => _handle.Shutdown(WinDivertShutdownMode.Recv);

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();
}

/// <summary>監視ハンドル（NETWORK、SNIFF | RECV_ONLY、priority -1000）を <see cref="IPacketSource"/> として公開する。</summary>
public sealed class WinDivertSniffSource : IPacketSource
{
    /// <summary>監視ハンドルの priority（ADR-0002: 制御ハンドル 0 より低くする）。</summary>
    public const short Priority = -1000;

    private const string Filter = "tcp or udp";
    private readonly WinDivertHandle _handle;
    private readonly WinDivertAddress[] _addresses = new WinDivertAddress[WinDivertConstants.BatchMax];

    private WinDivertSniffSource(WinDivertHandle handle)
    {
        _handle = handle;
    }

    /// <summary>監視ハンドルを開き、キューを最大に設定する（SNIFF なので溢れても通信は止まらない）。</summary>
    public static WinDivertSniffSource Open(ILogger logger)
    {
        var handle = WinDivertHandle.Open(Filter, WinDivertLayer.Network, Priority, WinDivertFlags.Sniff | WinDivertFlags.RecvOnly, logger);
        try
        {
            handle.SetParam(WinDivertParam.QueueLength, WinDivertConstants.QueueLengthMax);
            handle.SetParam(WinDivertParam.QueueSize, WinDivertConstants.QueueSizeMax);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WinDivertSniffSource: SetParam failed, closing handle");
            handle.Dispose();
            throw;
        }

        return new WinDivertSniffSource(handle);
    }

    /// <summary>ドライバのバージョン。</summary>
    public (ulong Major, ulong Minor) DriverVersion => _handle.GetDriverVersion();

    /// <summary>ERROR_INSUFFICIENT_BUFFER で捨てたバッチ数。</summary>
    public long InsufficientBufferCount => _handle.InsufficientBufferCount;

    /// <summary>ハンドルが開いているか。</summary>
    public bool IsOpen => _handle.IsOpen;

    /// <inheritdoc />
    public bool TryReceiveBatch(Span<byte> buffer, Span<PacketMeta> metas, out int bytesReceived, out int packetCount)
    {
        bytesReceived = 0;
        packetCount = 0;
        int max = Math.Min(metas.Length, _addresses.Length);
        if (!_handle.TryReceive(buffer, _addresses.AsSpan(0, max), out uint received, out int count))
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            ref var a = ref _addresses[i];
            metas[i] = new PacketMeta(a.Timestamp, a.Outbound, a.Loopback, a.IPv6);
        }

        bytesReceived = (int)received;
        packetCount = count;
        return true;
    }

    /// <inheritdoc />
    public void Shutdown() => _handle.Shutdown(WinDivertShutdownMode.Recv);

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();
}
