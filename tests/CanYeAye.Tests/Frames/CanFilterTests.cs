using CanYeAye.Core;

namespace CanYeAye.Tests.Frames;

public class CanFilterTests
{
    [Fact]
    public void ExactMatchWithFullMask()
    {
        var filter = new CanFilter(0x123, 0x1FFFFFFF, 0);
        Assert.True(filter.CheckFilter(0x123, 0));
        Assert.False(filter.CheckFilter(0x124, 0));
    }

    [Fact]
    public void MaskedMatch()
    {
        var filter = new CanFilter(0x700, 0x700, 2);
        Assert.True(filter.CheckFilter(0x7AB, 2));
        Assert.False(filter.CheckFilter(0x1AB, 2));
    }

    [Fact]
    public void FrameBusMinusOneMatchesAnyFilterBus()
    {
        // SavvyCAN: a frame with no bus (-1) matches every filter.
        var filter = new CanFilter(0, 0x1FFFFFFF, 4);
        Assert.True(filter.CheckFilter(0, -1));
        Assert.False(filter.CheckFilter(0, 0));
    }

    [Fact]
    public void SpecificBusMustMatch()
    {
        var filter = new CanFilter(0, 0x1FFFFFFF, 3);
        Assert.True(filter.CheckFilter(0, 3));
        Assert.False(filter.CheckFilter(0, 2));
    }
}

public class CanIdParserTests
{
    [Theory]
    [InlineData("0x1AB", 0x1ABUL)]
    [InlineData("X1AB", 0x1ABUL)]
    [InlineData("0b1010", 10UL)]
    [InlineData("B1010", 10UL)]
    [InlineData("291", 291UL)] // no prefix means decimal, like SavvyCAN
    [InlineData("garbage", 0UL)]
    public void ParsesIdsLikeSavvyCan(string text, ulong expected)
    {
        Assert.Equal(expected, CanIdParser.Parse(text));
    }

    [Theory]
    [InlineData("7FF", 0x7FFU)]
    [InlineData("1AB", 0x1ABU)]
    [InlineData("0x1AB", 0x1ABU)]
    public void ParseIdTreatsBareTextAsHex(string text, uint expected)
    {
        Assert.Equal(expected, CanIdParser.ParseId(text));
    }

    [Theory]
    [InlineData(0x7FFU, false, "7FF")]
    [InlineData(0x18DAF110U, true, "18DAF110")]
    public void FormatsIds(uint id, bool extended, string expected)
    {
        Assert.Equal(expected, CanIdParser.FormatId(id, extended));
    }
}
