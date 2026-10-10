using System.Windows.Input;
using DynamicBay.Core;

namespace DynamicBay.Tests;

public class HotkeyDigitTests
{
    [Theory]
    [InlineData("Ctrl+Alt+5", 0x35u)]
    [InlineData("Ctrl+Alt+0", 0x30u)]
    [InlineData("Strg+Umschalt+3", 0x33u)] // not (Key)3 = Tab
    [InlineData("Ctrl+Alt+D7", 0x37u)]
    public void Digits_map_to_the_number_keys(string text, uint expected)
    {
        Assert.True(Hotkey.TryParse(text, out _, out uint vk));
        Assert.Equal(expected, vk);
    }

    [Theory]
    [InlineData("Ctrl+12")]   // a number is no key name
    [InlineData("Ctrl+-1")]
    [InlineData("Ctrl+None")]
    public void Rejects_numbers_as_key_names(string text) => Assert.False(Hotkey.TryParse(text, out _, out _));

    [Fact]
    public void Formatted_digit_parses_back()
    {
        Assert.True(Hotkey.TryParse(Hotkey.Format(ModifierKeys.Control | ModifierKeys.Shift, Key.D9), out uint mods, out uint vk));
        Assert.Equal(Native.MOD_CONTROL | Native.MOD_SHIFT, mods);
        Assert.Equal(0x39u, vk);
    }
}
