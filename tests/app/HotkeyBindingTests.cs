using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

public class HotkeyBindingTests
{
    [Fact]
    public void Parse_CtrlAltW_ProducesExpectedModifiersAndKey()
    {
        var b = HotkeyBinding.Parse("Ctrl+Alt+W");
        b.Should().NotBeNull();
        b!.Modifiers.Should().Be(HotkeyBinding.ModControl | HotkeyBinding.ModAlt);
        b.VirtualKey.Should().Be((uint)'W');
    }

    [Fact]
    public void Parse_CaseInsensitive()
    {
        var b = HotkeyBinding.Parse("ctrl+ALT+w");
        b.Should().NotBeNull();
        b!.Modifiers.Should().Be(HotkeyBinding.ModControl | HotkeyBinding.ModAlt);
        b.VirtualKey.Should().Be((uint)'W');
    }

    [Fact]
    public void Parse_WinSpace()
    {
        var b = HotkeyBinding.Parse("Win+Space");
        b.Should().NotBeNull();
        b!.Modifiers.Should().Be(HotkeyBinding.ModWin);
        b.VirtualKey.Should().Be(0x20u);
    }

    [Fact]
    public void Parse_FKeys()
    {
        var b = HotkeyBinding.Parse("Ctrl+F3");
        b.Should().NotBeNull();
        b!.VirtualKey.Should().Be(0x72u); // VK_F3
    }

    [Fact]
    public void Parse_MissingKey_ReturnsNull()
    {
        HotkeyBinding.Parse("Ctrl+Alt").Should().BeNull();
    }

    [Fact]
    public void Parse_UnknownToken_ReturnsNull()
    {
        HotkeyBinding.Parse("Ctrl+Banana").Should().BeNull();
    }

    [Fact]
    public void ToString_RoundTrips()
    {
        var b = HotkeyBinding.Parse("Ctrl+Shift+E")!;
        b.ToString().Should().Be("Ctrl+Shift+E");
    }
}
