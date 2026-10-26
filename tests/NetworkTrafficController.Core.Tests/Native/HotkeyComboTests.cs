using NetworkTrafficController.Core.Native;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Native;

public class HotkeyComboTests
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;

    [Fact]
    public void Parse_CtrlAltLetter()
    {
        var c = HotkeyCombo.Parse("ctrl+alt+b");
        Assert.Equal(ModNoRepeat | ModControl | ModAlt, c.Modifiers);
        Assert.Equal((uint)'B', c.VirtualKey);
    }

    [Fact]
    public void Parse_FunctionKey()
    {
        var c = HotkeyCombo.Parse("shift+f9");
        Assert.Equal(ModNoRepeat | ModShift, c.Modifiers);
        Assert.Equal(0x70u + 8, c.VirtualKey); // VK_F9
    }

    [Fact]
    public void Parse_Digit_NoModifiers_StillHasNoRepeat()
    {
        var c = HotkeyCombo.Parse("5");
        Assert.Equal(ModNoRepeat, c.Modifiers);
        Assert.Equal((uint)'5', c.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ctrl+alt")]     // キーが無い
    [InlineData("ctrl+a+b")]     // キーが 2 つ
    [InlineData("ctrl+f99")]     // 範囲外
    [InlineData("ctrl+;")]       // 未対応キー
    public void Parse_Invalid_Throws(string text)
    {
        Assert.ThrowsAny<Exception>(() => HotkeyCombo.Parse(text));
    }
}
