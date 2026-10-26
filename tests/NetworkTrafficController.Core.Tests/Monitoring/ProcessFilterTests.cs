using NetworkTrafficController.Core.Monitoring;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Monitoring;

public class ProcessFilterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyFilter_MatchesEverything(string? filter)
    {
        Assert.True(ProcessFilter.Matches(filter, "chrome.exe", 19132));
    }

    [Theory]
    [InlineData("chrome", true)]
    [InlineData("CHROME", true)]
    [InlineData("rome.ex", true)]
    [InlineData(".exe", true)]
    [InlineData("  chrome  ", true)]
    [InlineData("firefox", false)]
    [InlineData("chrome exe", false)]
    public void Name_IsCaseInsensitiveSubstring(string filter, bool expected)
    {
        Assert.Equal(expected, ProcessFilter.Matches(filter, "chrome.exe", 19132));
    }

    [Theory]
    [InlineData("19132", true)]
    [InlineData("913", true)]
    [InlineData("1", true)]
    [InlineData("19133", false)]
    [InlineData("0", false)]
    [InlineData("019", false)]
    public void Pid_IsDecimalSubstring(string filter, bool expected)
    {
        Assert.Equal(expected, ProcessFilter.Matches(filter, "chrome.exe", 19132));
    }

    [Fact]
    public void EmptyName_MatchesOnlyByPid()
    {
        Assert.True(ProcessFilter.Matches("42", string.Empty, 4242));
        Assert.False(ProcessFilter.Matches("a", string.Empty, 4242));
    }

    [Fact]
    public void FullWidthSpaceOnly_MatchesEverything()
    {
        Assert.True(ProcessFilter.Matches("　", "chrome.exe", 19132));
    }

    [Fact]
    public void DigitsInName_MatchByNameToo()
    {
        Assert.True(ProcessFilter.Matches("7z", "7zFM.exe", 100));
    }
}
