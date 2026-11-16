using CanYeAye.Core;
using CanYeAye.Core.FileFormats;

namespace CanYeAye.Tests.FileFormats;

/// <summary>
/// Loads the reference example logs (copied from SavvyCAN's <c>examples</c>) and checks frame
/// counts plus representative frames. The fixtures all look like the same capture written in
/// different formats, so many formats share the 166.064 s / ID 0x21A opening frame.
/// </summary>
public class FixtureLoadTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static List<CanFrame> Load(string file)
    {
        var frames = new List<CanFrame>();
        Assert.True(LogFormatRegistry.AutoDetectLoad(Fixture(file), frames), $"failed to load {file}");
        return frames;
    }

    [Fact]
    public void NativeCsvFixture()
    {
        var frames = Load("GVRET_Log.csv");

        Assert.Equal(12974, frames.Count);
        var first = frames[0];
        Assert.Equal(166_064_000, first.Timestamp.ToMicroseconds());
        Assert.Equal(0x21AU, first.FrameId);
        Assert.False(first.IsExtended);
        Assert.Equal(0, first.Bus);
        Assert.Equal(new byte[] { 0xFE, 0x36, 0x12, 0xFE, 0x69, 0x05, 0x07, 0xAD }, first.Payload);

        var last = frames[^1];
        Assert.Equal(174_624_000, last.Timestamp.ToMicroseconds());
        Assert.Equal(0x102U, last.FrameId);
        Assert.Equal(6, last.Length);
    }

    [Fact]
    public void CrtdFixture()
    {
        var frames = Load("CRTD_Log.txt");

        Assert.Equal(12974, frames.Count);
        Assert.Equal(166_064_000, frames[0].Timestamp.ToMicroseconds());
        Assert.Equal(0x21AU, frames[0].FrameId);
        Assert.True(frames[0].IsReceived);
        Assert.Equal(8, frames[0].Length);
    }

    [Fact]
    public void BusMasterFixture()
    {
        var frames = Load("BusMasterLog.log");

        Assert.Equal(12974, frames.Count);
        var first = frames[0];
        // 22:20:14:992 as real microseconds (the reference multiplies by 100).
        Assert.Equal(80_414_992_000L, first.Timestamp.ToMicroseconds());
        Assert.Equal(0x21AU, first.FrameId);
        Assert.Equal(new byte[] { 0xFE, 0x36, 0x12, 0xFE, 0x69, 0x05, 0x07, 0xAD }, first.Payload);
    }

    [Fact]
    public void MicrochipFixture()
    {
        var frames = Load("MicrochipLog.log");

        Assert.Equal(12974, frames.Count);
        Assert.Equal(166_064_000, frames[0].Timestamp.ToMicroseconds());
        Assert.Equal(0x21AU, frames[0].FrameId);
        Assert.Equal(8, frames[0].Length);
        Assert.True(frames[0].IsReceived);
    }

    [Fact]
    public void CandumpFixture()
    {
        var frames = Load("candump.log");

        Assert.Equal(12974, frames.Count);
        Assert.Equal(166_000_000, frames[0].Timestamp.ToMicroseconds());
        Assert.Equal(0x21AU, frames[0].FrameId);
        Assert.Equal(0, frames[0].Bus);
        Assert.Equal(new byte[] { 0xFE, 0x36, 0x12, 0xFE, 0x69, 0x05, 0x07, 0xAD }, frames[0].Payload);
    }

    [Fact]
    public void GenericCsvFixture()
    {
        var frames = Load("GenericID_Log.csv");

        Assert.Equal(12974, frames.Count);
        Assert.Equal(0x21AU, frames[0].FrameId);
        Assert.Equal(8, frames[0].Length);
        // No timestamps in this format: loader synthesises 5 ms steps.
        Assert.Equal(5000, frames[1].Timestamp.ToMicroseconds() - frames[0].Timestamp.ToMicroseconds());
    }

    [Fact]
    public void CanWiseFixture()
    {
        var frames = Load("CANWise.log");

        Assert.Equal(82, frames.Count);
        var first = frames[0];
        Assert.Equal(33_299_735, first.Timestamp.ToMicroseconds());
        Assert.Equal(0x14FF1605U, first.FrameId);
        Assert.True(first.IsExtended);
        Assert.Equal(new byte[] { 0x7D, 0x7D, 0x7D, 0x7D, 0xEE, 0x00, 0xE8, 0x03 }, first.Payload);
    }

    [Fact]
    public void IxxatCsvFixture()
    {
        var frames = Load("SomeIXXAT.csv");

        Assert.Equal(21, frames.Count);
        var first = frames[0];
        Assert.Equal(0x19DU, first.FrameId);
        Assert.Equal(1, first.Bus);
        Assert.Equal(new byte[] { 0x40, 0x00, 0x3F, 0xFF, 0x00, 0x0E, 0xBD, 0xFF }, first.Payload);

        // Rows are relative to the file's "Start Time" preamble, so only the deltas are stable.
        Assert.Equal(1000,
            frames[2].Timestamp.ToMicroseconds() - first.Timestamp.ToMicroseconds());
        Assert.True(frames[^1].Timestamp.ToMicroseconds() > first.Timestamp.ToMicroseconds());
    }

    [Theory]
    [InlineData("CarBusAnalyzer.trc", 55, 46_016_000L)]
    [InlineData("CarBusAnalyzer_v3.trc", 84, 27_337_000L)]
    [InlineData("CarBusAnalyzer_CANFD.trc", 15, 469_067L)]
    public void CarBusFixtures(string file, int count, long firstTimestamp)
    {
        var frames = Load(file);

        Assert.Equal(count, frames.Count);
        Assert.Equal(firstTimestamp, frames[0].Timestamp.ToMicroseconds());
        Assert.Equal(1, frames[0].Bus);
        Assert.True(frames[0].IsReceived);
    }

    [Fact]
    public void CarBusCanFdKeepsSixteenByteFrames()
    {
        var frames = Load("CarBusAnalyzer_CANFD.trc");

        var frame = frames.First(f => f.Length == 16);
        Assert.Equal(0x319U, frame.FrameId);
        Assert.Equal(16, frame.Length);
        Assert.True(frame.IsFlexibleDataRate);
    }
}

