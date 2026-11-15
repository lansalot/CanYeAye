using WinCAN.Core.Formatting;

namespace WinCAN.Tests.Frames;

public class HexFormatTests
{
    [Fact]
    public void FormatsBytesWithSingleSpaces()
    {
        Assert.Equal("FE 36 12 00", HexFormat.SpaceDelimited([0xFE, 0x36, 0x12, 0x00]));
    }

    [Fact]
    public void SingleByteHasNoSpaces()
    {
        Assert.Equal("0A", HexFormat.SpaceDelimited([0x0A]));
    }

    [Fact]
    public void EmptyPayloadIsEmptyString()
    {
        Assert.Equal(string.Empty, HexFormat.SpaceDelimited([]));
    }
}
