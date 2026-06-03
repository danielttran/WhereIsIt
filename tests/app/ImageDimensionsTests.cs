using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class ImageDimensionsTests
{
    [Fact]
    public void TryParse_Png_ReadsWidthAndHeight()
    {
        byte[] png =
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // signature
            0x00, 0x00, 0x00, 0x0D,                         // IHDR length
            0x49, 0x48, 0x44, 0x52,                         // "IHDR"
            0x00, 0x00, 0x00, 0x02,                         // width = 2
            0x00, 0x00, 0x00, 0x03,                         // height = 3
        };
        ImageDimensions.TryParse(png, png.Length, out var w, out var h).Should().BeTrue();
        w.Should().Be(2);
        h.Should().Be(3);
    }

    [Fact]
    public void TryParse_Gif_ReadsWidthAndHeight()
    {
        byte[] gif =
        {
            (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
            0x04, 0x00, // width = 4 (LE)
            0x05, 0x00, // height = 5 (LE)
        };
        ImageDimensions.TryParse(gif, gif.Length, out var w, out var h).Should().BeTrue();
        w.Should().Be(4);
        h.Should().Be(5);
    }

    [Fact]
    public void TryParse_Bmp_ReadsWidthAndHeight()
    {
        var bmp = new byte[26];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        bmp[18] = 6;  // width  (LE)
        bmp[22] = 7;  // height (LE)
        ImageDimensions.TryParse(bmp, bmp.Length, out var w, out var h).Should().BeTrue();
        w.Should().Be(6);
        h.Should().Be(7);
    }

    [Fact]
    public void TryParse_NonImage_ReturnsFalse()
    {
        byte[] junk = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };
        ImageDimensions.TryParse(junk, junk.Length, out _, out _).Should().BeFalse();
    }
}
