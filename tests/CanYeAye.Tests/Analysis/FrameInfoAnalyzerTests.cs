using CanYeAye.Core;
using CanYeAye.Core.Analysis;
using CanYeAye.Core.Dbc;

namespace CanYeAye.Tests.Analysis;

public class FrameInfoAnalyzerTests
{
    private static CanFrame Frame(uint id, long micros, params byte[] data)
    {
        var frame = new CanFrame(id, data);
        frame.Timestamp = FrameTimestamp.FromMicroseconds(micros);
        return frame;
    }

    private static CanFrame[] StatsFrames() =>
    [
        Frame(0x100, 0, 0x01, 0x80, 0xAA),
        Frame(0x200, 50_000, 0x77),
        Frame(0x100, 100_000, 0x03, 0x00, 0xAA),
        Frame(0x100, 300_000, 0x00, 0xFF, 0xAA),
    ];

    [Fact]
    public void CollectIdsReturnsSortedUniqueIds()
    {
        var capture = StatsFrames();

        Assert.Equal(new uint[] { 0x100, 0x200 }, FrameInfoAnalyzer.CollectIds(capture));
    }

    [Fact]
    public void AnalyzeReturnsNullWhenTheIdIsMissing()
    {
        Assert.Null(FrameInfoAnalyzer.Analyze(StatsFrames(), 0x300));
    }

    [Fact]
    public void PerByteStatisticsTrackRangeChangedBitsValueAndBitCounts()
    {
        var report = FrameInfoAnalyzer.Analyze(StatsFrames(), 0x100);

        Assert.NotNull(report);
        Assert.Equal(3, report.FrameCount);
        Assert.Equal(3, report.Bytes.Count);

        var byte0 = report.Bytes[0];
        Assert.Equal(0, byte0.Minimum);
        Assert.Equal(3, byte0.Maximum);
        Assert.Equal(0x03, byte0.ChangedBits);
        Assert.Equal(1, byte0.ValueCounts[0x00]);
        Assert.Equal(1, byte0.ValueCounts[0x01]);
        Assert.Equal(1, byte0.ValueCounts[0x03]);
        Assert.Equal(2, byte0.BitSetCounts[0]);
        Assert.Equal(1, byte0.BitSetCounts[1]);
        Assert.Equal(1.0 / 3.0, byte0.BitChangeRates[0], 10);
        Assert.Equal(2.0 / 3.0, byte0.BitChangeRates[1], 10);
        Assert.Equal(new byte[] { 0x01, 0x03, 0x00 }, byte0.Series);

        var byte1 = report.Bytes[1];
        Assert.Equal(0, byte1.Minimum);
        Assert.Equal(255, byte1.Maximum);
        Assert.Equal(0xFF, byte1.ChangedBits);
        Assert.Equal(2, byte1.BitSetCounts[7]);
        Assert.Equal(1, byte1.BitSetCounts[0]);
        Assert.Equal(2.0 / 3.0, byte1.BitChangeRates[7], 10);
        Assert.Equal(1.0 / 3.0, byte1.BitChangeRates[0], 10);

        var constant = report.Bytes[2];
        Assert.Equal(0, constant.ChangedBits);
        Assert.Equal(3, constant.ValueCounts[0xAA]);
        Assert.Equal(0.0, constant.BitChangeRates[0]);
    }

    [Fact]
    public void AggregateBitCountsAndHeatCoverSixtyFourBits()
    {
        var report = FrameInfoAnalyzer.Analyze(StatsFrames(), 0x100);

        Assert.NotNull(report);
        Assert.Equal(64, report.BitSetCounts.Length);
        Assert.Equal(64, report.BitChangeRates.Length);
        Assert.Equal(2, report.BitSetCounts[(1 * 8) + 7]);
        Assert.Equal(2.0 / 3.0, report.BitChangeRates[(1 * 8) + 7], 10);
    }

    [Fact]
    public void IntervalsUseWholeTimestampsAndProduceStatistics()
    {
        var report = FrameInfoAnalyzer.Analyze(StatsFrames(), 0x100);

        Assert.NotNull(report);
        var intervals = report.Intervals;
        Assert.True(intervals.HasIntervals);
        Assert.Equal(150_000, intervals.AverageMicroseconds);
        Assert.Equal(100_000, intervals.MinimumMicroseconds);
        Assert.Equal(200_000, intervals.MaximumMicroseconds);
        Assert.Equal(100_000, intervals.VariationMicroseconds);
        Assert.Equal(50_000, intervals.StandardDeviationMicroseconds);
        Assert.Equal(100_000, intervals.P90RangeMicroseconds);

        Assert.Equal(21, intervals.HistogramCounts.Length);
        Assert.Equal(2, intervals.HistogramCounts.Sum());
        Assert.Equal(1, intervals.HistogramCounts[0]);
        Assert.Equal(1, intervals.HistogramCounts[^1]);
        Assert.Equal(100.0, intervals.HistogramUpperMilliseconds[0], 6);
        Assert.Equal(200.0, intervals.HistogramUpperMilliseconds[^1], 6);
    }

    [Fact]
    public void IntervalsAreMeasuredAcrossSecondBoundaries()
    {
        var capture = new[]
        {
            Frame(0x80, 1_900_000, 0x00),
            Frame(0x80, 2_100_000, 0x00),
        };

        var report = FrameInfoAnalyzer.Analyze(capture, 0x80);

        Assert.NotNull(report);
        Assert.Equal(200_000, report.Intervals.MinimumMicroseconds);
        Assert.Equal(200_000, report.Intervals.MaximumMicroseconds);
        Assert.Equal(200_000, report.Intervals.AverageMicroseconds);
    }

    [Fact]
    public void SingleFrameIdHasNoIntervalsInsteadOfIntMax()
    {
        var report = FrameInfoAnalyzer.Analyze([Frame(0x7FF, 0, 0x00)], 0x7FF);

        Assert.NotNull(report);
        Assert.False(report.Intervals.HasIntervals);
        Assert.Equal(0, report.Intervals.MinimumMicroseconds);
        Assert.Equal(0, report.Intervals.AverageMicroseconds);
        Assert.Empty(report.Intervals.HistogramCounts);
    }

    [Fact]
    public void DataLengthTracksTheShortestAndLongestFrame()
    {
        var capture = new[]
        {
            Frame(0x123, 0, 0x01, 0x02, 0x03),
            Frame(0x123, 1_000, 0x01, 0x02, 0x03, 0x04, 0x05),
        };

        var report = FrameInfoAnalyzer.Analyze(capture, 0x123);

        Assert.NotNull(report);
        Assert.Equal(3, report.MinimumLength);
        Assert.Equal(5, report.MaximumLength);
        Assert.Equal(5, report.Bytes.Count);
        Assert.Equal(new byte[] { 0x01, 0x01 }, report.Bytes[0].Series);
        Assert.Equal(0, report.Bytes[3].ValueCounts[0]);
        Assert.Equal(1, report.Bytes[3].ValueCounts[0x04]);
    }

    [Fact]
    public void MoreThanEightBytesOnlyProducesEightByteStats()
    {
        var data = new byte[12];
        var report = FrameInfoAnalyzer.Analyze([Frame(0x321, 0, data)], 0x321);

        Assert.NotNull(report);
        Assert.Equal(12, report.MaximumLength);
        Assert.Equal(FrameInfoAnalyzer.StatBytes, report.Bytes.Count);
    }

    [Fact]
    public void ExtendedIdsGetJ1939AndGmlanDetailNodes()
    {
        const uint id = 0x18EAFFF9;
        var frame = Frame(id, 0, 0x00);
        frame.SetExtended(true);

        var report = FrameInfoAnalyzer.Analyze([frame], id);

        Assert.NotNull(report);
        var j1939 = report.Root.Children.Single(node => node.Text == "J1939 decoding");
        Assert.Contains("Destination ID: Global (ALL)", j1939.Children.Select(node => node.Text));
        Assert.Contains("Priority: 6 (0-7)", j1939.Children.Select(node => node.Text));
        Assert.Contains("SRC: 0xF9", j1939.Children.Select(node => node.Text));
        Assert.Contains("EDP: 0", j1939.Children.Select(node => node.Text));
        Assert.Contains("DP: 0", j1939.Children.Select(node => node.Text));
        Assert.Contains("PGN: 0xEA00 (59904)", j1939.Children.Select(node => node.Text));
        Assert.Contains("PF: 0xEA", j1939.Children.Select(node => node.Text));
        Assert.Contains("PS: 0xFF", j1939.Children.Select(node => node.Text));

        var gmlan = report.Root.Children.Single(node => node.Text == "GMLAN 29bit decoding");
        Assert.Contains("Priority bits: 0x06", gmlan.Children.Select(node => node.Text));
        Assert.Contains("Arbitration Id: 0x0757", gmlan.Children.Select(node => node.Text));
        Assert.Contains("Sender Id: 0x1FF9", gmlan.Children.Select(node => node.Text));
    }

    [Fact]
    public void BroadcastJ1939IdsAreLabelledAsBroadcast()
    {
        const uint id = 0x18FEF100;
        var frame = Frame(id, 0, 0x00);
        frame.SetExtended(true);

        var report = FrameInfoAnalyzer.Analyze([frame], id);

        Assert.NotNull(report);
        var j1939 = report.Root.Children.Single(node => node.Text == "J1939 decoding");
        Assert.Contains("Broadcast Frame", j1939.Children.Select(node => node.Text));
        Assert.Equal("PGN: 0xFEF1 (65265)", j1939.Children.Single(node => node.Text.StartsWith("PGN: ")).Text);
    }

    [Fact]
    public void DetailTreeContainsTheReferenceSectionLines()
    {
        var report = FrameInfoAnalyzer.Analyze(StatsFrames(), 0x100, label: "0x100 Test");

        Assert.NotNull(report);
        Assert.Equal("ID: 0x100 Test", report.Root.Text);
        Assert.Contains("# of frames: 3", report.Root.Children.Select(node => node.Text));
        Assert.Contains("Data Length: 3", report.Root.Children.Select(node => node.Text));
        Assert.Contains("Data Byte 0", report.Root.Children.Select(node => node.Text));
        Assert.Contains("Bitfield Histogram", report.Root.Children.Select(node => node.Text));
        Assert.Contains("Bit Change Heatmap", report.Root.Children.Select(node => node.Text));

        var byte0 = report.Root.Children.Single(node => node.Text == "Data Byte 0");
        Assert.Contains("Changed bits: 0x03  (00000011)", byte0.Children.Select(node => node.Text));
        Assert.Contains("Range: 0x00 to 0x03", byte0.Children.Select(node => node.Text));

        var histogram = byte0.Children.Single(node => node.Text == "Histogram");
        Assert.Contains("3/0x03 (00000011) -> 1", histogram.Children.Select(node => node.Text));

        var text = report.ToText();
        Assert.Contains("\tData Byte 0\n", text);
        Assert.Contains("\t\tChanged bits: 0x03  (00000011)\n", text);
        Assert.Contains("\t\t\t3/0x03 (00000011) -> 1\n", text);
    }

    [Fact]
    public void DbCSignalValuesAggregateIntoHistograms()
    {
        var message = new DbcMessage { Id = 0x100, Name = "TestMessage" };
        var signal = new DbcSignal
        {
            Name = "Speed",
            StartBit = 0,
            SignalSize = 8,
            IntelByteOrder = true,
            ValueType = DbcSignalValueType.UnsignedInt,
            Factor = 1.0,
            UnitName = "km/h",
            ParentMessage = message,
        };
        message.Signals[signal.Name] = signal;

        var capture = new[]
        {
            Frame(0x100, 0, 10, 0x00),
            Frame(0x100, 1_000, 20, 0x00),
            Frame(0x100, 2_000, 10, 0x00),
        };

        var report = FrameInfoAnalyzer.Analyze(capture, 0x100, message);

        Assert.NotNull(report);
        var speed = Assert.Single(report.Signals);
        Assert.Equal("Speed", speed.SignalName);
        Assert.Equal(
            new[] { new FrameInfoValueCount("10 km/h", 2), new FrameInfoValueCount("20 km/h", 1) },
            speed.Values);
        Assert.Contains("Speed", report.Root.Children.Select(node => node.Text));
    }
}
