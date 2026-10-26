using NetworkTrafficController.Core.Monitoring;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Monitoring;

public class ProcessSelectionTextTests
{
    private static (string, uint)[] P(params string[] names) =>
        names.Select((n, i) => (n, (uint)(100 + i))).ToArray();

    [Fact]
    public void None_IsEmpty()
    {
        Assert.Equal(string.Empty, ProcessSelectionText.Short([]));
    }

    [Fact]
    public void One_ShowsNameAndPid()
    {
        Assert.Equal("chrome.exe (PID 19132)", ProcessSelectionText.Short([("chrome.exe", 19132u)]));
    }

    [Fact]
    public void UpToThree_ListsNamesWithoutPid()
    {
        Assert.Equal("chrome.exe, ssh.exe, Code.exe", ProcessSelectionText.Short(P("chrome.exe", "ssh.exe", "Code.exe")));
    }

    [Fact]
    public void SameNames_AreGroupedWithCount()
    {
        Assert.Equal("Code.exe ×3, ssh.exe", ProcessSelectionText.Short(P("Code.exe", "ssh.exe", "code.exe", "Code.exe")));
    }

    [Fact]
    public void MoreThanThreeNames_ShowsFirstTwoAndTheRest()
    {
        Assert.Equal("chrome.exe, ssh.exe +3 more", ProcessSelectionText.Short(P("chrome.exe", "ssh.exe", "Code.exe", "steam.exe", "claude.exe")));
    }

    [Fact]
    public void MoreCount_CountsProcessesNotNames()
    {
        // 残りは steam.exe と Code.exe ×2 で、プロセスとしては 3 つ
        Assert.Equal("chrome.exe, ssh.exe +3 more", ProcessSelectionText.Short(P("chrome.exe", "ssh.exe", "Code.exe", "Code.exe", "steam.exe")));
    }

    [Fact]
    public void Full_ListsEveryProcessWithPid()
    {
        Assert.Equal("a.exe (PID 1)\nb.exe (PID 2)", ProcessSelectionText.Full([("a.exe", 1u), ("b.exe", 2u)]));
    }
}
