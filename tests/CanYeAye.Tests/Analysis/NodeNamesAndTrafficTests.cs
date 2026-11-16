using CanYeAye.Core;
using CanYeAye.Core.Analysis;

namespace CanYeAye.Tests.Analysis;

public class J1939NodeNamesTests
{
    [Fact]
    public void NamesOverrideTheDefaultAddressLabel()
    {
        var names = new J1939NodeNames();

        Assert.Equal("0x0C", names.GetLabel(0x0C));

        names.SetName(0x01, "Steering module");

        Assert.Equal("Steering module", names.GetLabel(0x01));
        Assert.Equal("0x01", J1939NodeNames.FormatAddress(0x01));
    }

    [Fact]
    public void SaveAndLoadRoundTripNamesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"canyeaye-nodes-{Guid.NewGuid():N}.txt");
        try
        {
            var names = new J1939NodeNames();
            names.SetName(0x01, "Steering module");
            names.SetName(0x02, "ABS");
            names.SetName(0xFE, "Diagnostics");
            names.Save(path);

            var loaded = new J1939NodeNames();
            Assert.True(loaded.Load(path));

            Assert.Equal(3, loaded.Count);
            Assert.Equal("Steering module", loaded[0x01]);
            Assert.Equal("ABS", loaded[0x02]);
            Assert.Equal("Diagnostics", loaded[0xFE]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAcceptsCommentsDecimalAddressesAndWhitespace()
    {
        var path = Path.Combine(Path.GetTempPath(), $"canyeaye-nodes-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllLines(path,
            [
                "# nodes",
                "",
                "0x10 = Engine ",
                "32=Transmission",
                "garbage line",
                "0x11=",
            ]);

            var names = new J1939NodeNames();
            Assert.True(names.Load(path));

            Assert.Equal("Engine", names[0x10]);
            Assert.Equal("Transmission", names[32]);
            Assert.Equal(2, names.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ClearingANameRemovesIt()
    {
        var names = new J1939NodeNames();
        names.SetName(0x05, "Brake");
        names.SetName(0x05, "   ");

        Assert.Null(names[0x05]);
        Assert.Equal(0, names.Count);
    }

    [Fact]
    public void NodeTrafficCountsBroadcastAndPointToPoint()
    {
        var traffic = new NodeTraffic();

        // PDU2 (PF=0xFE): always broadcast, source 0x10.
        traffic.Add(Frame(0x18FE0010));
        // PDU1 to destination 0x20, source 0x10.
        traffic.Add(Frame(0x18EA2010));
        traffic.Add(Frame(0x18EA2010));
        // PDU1 to the global address 0xFF is a broadcast too.
        traffic.Add(Frame(0x18EAFF10));
        // Standard 11-bit frames carry no J1939 source.
        var standard = Frame(0x123);
        standard.SetExtended(false);
        traffic.Add(standard);

        Assert.Equal(4, traffic.TotalFrames);
        Assert.Equal(1, traffic.IgnoredFrames);
        Assert.Equal(4, traffic.NodeFrames[0x10]);
        Assert.Equal(2, traffic.BroadcastFrames[0x10]);
        Assert.Equal(2, traffic.Conversations[(0x10, 0x20)]);
        Assert.Contains((0x10, 0x20), traffic.Conversations.Keys);
    }

    [Fact]
    public void BidirectionalTrafficIsCombinedPerPair()
    {
        var traffic = new NodeTraffic();
        traffic.Add(Frame(0x18EA2010)); // 0x10 -> 0x20
        traffic.Add(Frame(0x18EA1020)); // 0x20 -> 0x10

        Assert.Equal(2, traffic.BidirectionalFrames(0x10, 0x20));
        Assert.Equal(2, traffic.BidirectionalFrames(0x20, 0x10));
        Assert.Equal(1, traffic.NodeFrames[0x10]);
        Assert.Equal(1, traffic.NodeFrames[0x20]);
    }

    private static CanFrame Frame(uint id)
    {
        var frame = new CanFrame();
        frame.SetFrameId(id);
        frame.SetExtended(true);
        frame.SetPayload([1, 2, 3, 4, 5, 6, 7, 8]);
        frame.Timestamp = FrameTimestamp.FromMicroseconds(1000);
        return frame;
    }
}
