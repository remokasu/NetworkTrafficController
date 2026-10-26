using NetworkTrafficController.Core.Exceptions;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Exceptions;

public class WinDivertExceptionTests
{
    [Theory]
    [InlineData(2, WinDivertFailure.DriverFileNotFound)]
    [InlineData(5, WinDivertFailure.AccessDenied)]
    [InlineData(433, WinDivertFailure.DriverDeviceUnavailable)]
    [InlineData(1058, WinDivertFailure.DriverServiceDisabled)]
    [InlineData(577, WinDivertFailure.InvalidDriverSignature)]
    [InlineData(654, WinDivertFailure.IncompatibleDriverLoaded)]
    [InlineData(99999, WinDivertFailure.Unknown)]
    public void Classify_MapsWin32Errors(int win32Error, WinDivertFailure expected)
    {
        Assert.Equal(expected, WinDivertException.Classify(win32Error));
    }

    [Theory]
    [InlineData(WinDivertFailure.DriverDeviceUnavailable, true)]
    [InlineData(WinDivertFailure.DriverServiceDisabled, true)]
    [InlineData(WinDivertFailure.AccessDenied, false)]
    [InlineData(WinDivertFailure.DriverFileNotFound, false)]
    [InlineData(WinDivertFailure.Unknown, false)]
    public void IsTransient_OnlyForStatesThatClearByWaiting(WinDivertFailure failure, bool expected)
    {
        Assert.Equal(expected, WinDivertException.IsTransient(failure));
    }

    [Fact]
    public void Describe_DeviceUnavailable_TellsUserToRestart()
    {
        // 停止処理中で固まったドライバは再起動でしか戻らないので、未分類ではなく対処を示す
        Assert.Contains("restart", WinDivertException.Describe(WinDivertFailure.DriverDeviceUnavailable), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restart", WinDivertException.Describe(WinDivertFailure.DriverServiceDisabled), StringComparison.OrdinalIgnoreCase);
    }
}
