using NetTrans.Services;
using Xunit;

namespace NetTrans.Tests;

/// <summary>老板键, stored the way the sheet displays it.</summary>
public class HotKeyBindingTests
{
    [Fact]
    public void The_default_is_the_one_the_sheet_ships_with()
    {
        var parsed = HotKeyBinding.Parse("Ctrl + Alt + H");

        Assert.Equal(HotKeyBinding.Default, parsed);
        Assert.Equal(HotKeyModifiers.Control | HotKeyModifiers.Alt, parsed!.Value.Modifiers);
        Assert.Equal('H', parsed.Value.VirtualKey);
    }

    [Theory]
    [InlineData("ctrl+alt+h")]
    [InlineData("CTRL + ALT + H")]
    [InlineData("Control+Alt+H")]
    [InlineData("  Ctrl  +  Alt  +  h  ")]
    public void Spacing_and_case_do_not_matter(string text) =>
        Assert.Equal(HotKeyBinding.Default, HotKeyBinding.Parse(text));

    [Fact]
    public void Function_keys_and_digits_work_too()
    {
        Assert.Equal(0x70 + 11, HotKeyBinding.Parse("Ctrl + F12")!.Value.VirtualKey);
        Assert.Equal(0x70, HotKeyBinding.Parse("Alt + F1")!.Value.VirtualKey);
        Assert.Equal('7', HotKeyBinding.Parse("Win + Shift + 7")!.Value.VirtualKey);
        Assert.Equal(0x20, HotKeyBinding.Parse("Ctrl + Space")!.Value.VirtualKey);
    }

    [Fact]
    public void Every_modifier_maps_to_its_win32_flag()
    {
        var all = HotKeyBinding.Parse("Ctrl + Alt + Shift + Win + K")!.Value;

        Assert.Equal(
            HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift | HotKeyModifiers.Windows,
            all.Modifiers);

        // The flags are Win32's own MOD_* values, which the shell relies on.
        Assert.Equal(0x0001 | 0x0002 | 0x0004 | 0x0008, (int)all.Modifiers);
    }

    [Theory]
    [InlineData("H")]                  // no modifier would swallow the key system-wide
    [InlineData("Ctrl")]               // no key
    [InlineData("Ctrl + Alt")]
    [InlineData("Ctrl + H + J")]       // two keys is not a combination
    [InlineData("Ctrl + F25")]
    [InlineData("Ctrl + 表")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_combination_that_would_not_register_is_refused(string? text) =>
        Assert.Null(HotKeyBinding.Parse(text));

    [Theory]
    [InlineData("Ctrl + Alt + H")]
    [InlineData("Ctrl + F12")]
    [InlineData("Ctrl + Alt + Shift + Win + K")]
    [InlineData("Alt + Space")]
    public void It_round_trips_back_to_the_sheets_spelling(string text) =>
        Assert.Equal(text, HotKeyBinding.Parse(text)!.Value.ToString());
}
