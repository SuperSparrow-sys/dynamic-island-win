using DynamicBay.Core;

namespace DynamicBay.Tests;

public class DecodeSizeTests
{
    [Theory]
    [InlineData(1920, 1080, 320, 0)]   // wide: by width
    [InlineData(1080, 7000, 0, 320)]   // tall scrolling capture: by height, not 320 x 2074
    [InlineData(800, 800, 320, 0)]
    [InlineData(200, 120, 0, 0)]       // small already: not blown up
    [InlineData(320, 100, 0, 0)]
    [InlineData(0, 0, 320, 0)]         // size unknown: by width
    public void Thumbnails_are_decoded_by_their_longer_side(int w, int h, int dw, int dh) =>
        Assert.Equal((dw, dh), ImageTools.DecodeSize(w, h, 320));
}
