using NetworkTrafficController.Core.Exceptions;
using NetworkTrafficController.Core.Native;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Native;

public class DriverStartRetryPolicyTests
{
    private static WinDivertException Error(int win32) =>
        WinDivertException.FromWin32("WinDivertOpen", win32);

    [Theory]
    [InlineData(433)]   // ドライバが停止処理中
    [InlineData(1058)]  // 古いサービス登録が片付け途中
    public void TransientErrors_AreRetried_UntilTheLimit(int win32)
    {
        var policy = new DriverStartRetryPolicy(3, TimeSpan.FromSeconds(1));

        Assert.True(policy.ShouldRetry(Error(win32), 1));
        Assert.True(policy.ShouldRetry(Error(win32), 2));
        Assert.False(policy.ShouldRetry(Error(win32), 3));
    }

    [Theory]
    [InlineData(2)]     // ドライバのファイルが無い
    [InlineData(5)]     // 管理者権限が無い
    [InlineData(577)]   // 署名が無効
    [InlineData(99999)] // 未分類
    public void PermanentErrors_AreNotRetried(int win32)
    {
        Assert.False(DriverStartRetryPolicy.Default.ShouldRetry(Error(win32), 1));
    }

    [Fact]
    public void Default_WaitsAboutTwentySeconds()
    {
        var p = DriverStartRetryPolicy.Default;
        Assert.Equal(20, (p.MaxAttempts * p.Interval).TotalSeconds);
    }
}
