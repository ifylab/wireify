// SPDX-License-Identifier: Apache-2.0
using WireifyCore.Bridge;
using Xunit;

namespace WireifyCore.Tests;

/// <summary>Pure hex-colour contract for the colour-swatch control: the browser-facing string
/// forms in, the canonical lowercase form out, alpha only when it carries information.</summary>
public class ColourHexTests
{
    [Theory]
    [InlineData("#3366ff", 255, 0x33, 0x66, 0xff)]
    [InlineData("3366ff", 255, 0x33, 0x66, 0xff)] // leading # optional
    [InlineData("#36F", 255, 0x33, 0x66, 0xff)] // short form doubles digits
    [InlineData("#36f8", 0x88, 0x33, 0x66, 0xff)] // short form with alpha
    [InlineData("#3366ff80", 0x80, 0x33, 0x66, 0xff)]
    [InlineData("  #FF0000  ", 255, 0xff, 0x00, 0x00)] // whitespace + case tolerated
    public void Parses_the_web_hex_forms(string text, int a, int r, int g, int b)
    {
        Assert.True(ColourHex.TryParse(text, out var pa, out var pr, out var pg, out var pb));
        Assert.Equal((byte)a, pa);
        Assert.Equal((byte)r, pr);
        Assert.Equal((byte)g, pg);
        Assert.Equal((byte)b, pb);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#12345")] // 5 digits is no form
    [InlineData("#12345678aa")] // too long
    [InlineData("#gg0000")] // not hex
    [InlineData("red")] // named colours are the browser's business, not the bridge's
    public void Refuses_non_hex_text(string? text)
        => Assert.False(ColourHex.TryParse(text, out _, out _, out _, out _));

    [Fact]
    public void Formats_opaque_without_an_alpha_byte()
        => Assert.Equal("#3366ff", ColourHex.Format(255, 0x33, 0x66, 0xff));

    [Fact]
    public void Formats_translucent_with_the_alpha_byte()
        => Assert.Equal("#3366ff80", ColourHex.Format(0x80, 0x33, 0x66, 0xff));

    [Theory]
    [InlineData("#3366ff")]
    [InlineData("#3366ff80")]
    public void Round_trips_through_parse_and_format(string canonical)
    {
        Assert.True(ColourHex.TryParse(canonical, out var a, out var r, out var g, out var b));
        Assert.Equal(canonical, ColourHex.Format(a, r, g, b));
    }
}
