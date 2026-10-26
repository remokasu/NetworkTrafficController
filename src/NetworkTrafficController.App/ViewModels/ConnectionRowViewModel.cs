using CommunityToolkit.Mvvm.ComponentModel;
using NetworkTrafficController.Core.Mapping;
using NetworkTrafficController.Core.Monitoring;

namespace NetworkTrafficController.App.ViewModels;

/// <summary>選択プロセスの接続一覧の 1 行。</summary>
public sealed partial class ConnectionRowViewModel : ObservableObject
{
    [ObservableProperty] private long _outBytes;
    [ObservableProperty] private long _inBytes;

    /// <summary>行のキー（プロトコル + 5-tuple）。</summary>
    public (byte Protocol, NetAddress La, ushort Lp, NetAddress Ra, ushort Rp) Key { get; }

    /// <summary>この接続を持つプロセスの名前（複数のプロセスを選んだときに区別するため）。</summary>
    public string ProcessName { get; }

    public string Protocol { get; }
    public string Local { get; }
    public string Remote { get; }
    public bool Loopback { get; }

    public ConnectionRowViewModel(ConnectionTraffic c, string processName)
    {
        ProcessName = processName;
        Key = (c.Key.Protocol, c.Key.LocalAddress, c.Key.LocalPort, c.Key.RemoteAddress, c.Key.RemotePort);
        Protocol = IpProtocol.Name(c.Key.Protocol);
        Local = $"{c.Key.LocalAddress}:{c.Key.LocalPort}";
        Remote = $"{c.Key.RemoteAddress}:{c.Key.RemotePort}";
        Loopback = c.Loopback;
    }

    public string OutText => Formatting.Bytes(OutBytes);
    public string InText => Formatting.Bytes(InBytes);

    partial void OnOutBytesChanged(long value) => OnPropertyChanged(nameof(OutText));
    partial void OnInBytesChanged(long value) => OnPropertyChanged(nameof(InText));
}
