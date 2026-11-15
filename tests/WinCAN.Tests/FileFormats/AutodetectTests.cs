using WinCAN.Core;
using WinCAN.Core.FileFormats;

namespace WinCAN.Tests.FileFormats;

/// <summary>
/// Autodetect tests against the reference example logs copied from SavvyCAN's
/// <c>examples</c> folder. SavvyCAN's detectors are deliberately strict, and our ports must
/// accept the same files (and reject neighbouring formats).
/// </summary>
public class AutodetectTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Theory]
    [InlineData("GVRET_Log.csv", "GVRET Logs")]
    [InlineData("CRTD_Log.txt", "CRTD Logs")]
    [InlineData("BusMasterLog.log", "BusMaster Log")]
    [InlineData("MicrochipLog.log", "Microchip Log")]
    [InlineData("candump.log", "Candump/Kayak")]
    [InlineData("GenericID_Log.csv", "Generic ID/Data CSV")]
    [InlineData("SomeIXXAT.csv", "IXXAT CSV export")]
    [InlineData("CANWise.log", "CANWise Log")]
    [InlineData("CarBusAnalyzer.trc", "CARBUS Analyzer Trace Files")]
    [InlineData("CarBusAnalyzer_v3.trc", "CARBUS Analyzer Trace Files")]
    [InlineData("CarBusAnalyzer_CANFD.trc", "CARBUS Analyzer Trace Files")]
    public void DetectsExpectedFormat(string file, string expectedName)
    {
        var format = LogFormatRegistry.AutoDetect(Fixture(file));

        Assert.NotNull(format);
        Assert.Equal(expectedName, format!.Name);
    }

    [Fact]
    public void AutodetectLoadFillsFrames()
    {
        var frames = new List<CanFrame>();

        Assert.True(LogFormatRegistry.AutoDetectLoad(Fixture("GVRET_Log.csv"), frames));
        Assert.NotEmpty(frames);
    }

    [Fact]
    public void AutodetectRejectsUnknownText()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wincan-unknown-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "hello world\nthis is not a can log\n");

        try
        {
            Assert.Null(LogFormatRegistry.AutoDetect(path));
            Assert.False(LogFormatRegistry.AutoDetectLoad(path, []));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FailingFormatDoesNotLeakPartialFrames()
    {
        // CANWise's loader bails out on the first malformed data row; the registry must drop
        // the frames it had already appended.
        var path = Path.Combine(Path.GetTempPath(), $"wincan-partial-{Guid.NewGuid():N}.log");
        File.WriteAllText(
            path,
            "EV NUM/ERR F/F ID     LEN R/D   D0  D1  D2  D3  D4  D5  D6  D7 TIMESTAMP\n" +
            "RX 0000318 EFF 14FF1605 8 HEX   7D  7D  7D  7D  EE  00  E8  03 0033299735 07.05.2022\n" +
            "this row is garbage\n");

        try
        {
            var frames = new List<CanFrame>();
            Assert.False(LogFormatRegistry.Load(path, 27, frames));
            Assert.Empty(frames);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FilterTablesKeepSavvyCanIndices()
    {
        Assert.Equal(28, LogFormatRegistry.LoadFilters.Count);
        Assert.Equal(13, LogFormatRegistry.SaveFilters.Count);
        Assert.Equal("GVRET Logs", LogFormatRegistry.LoadFilters[1].Format?.Name);
        Assert.Equal("PCAN Viewer", LogFormatRegistry.LoadFilters[11].Format?.Name);
        Assert.Equal("CANWise Log", LogFormatRegistry.LoadFilters[27].Format?.Name);
        Assert.Equal("GVRET Logs", LogFormatRegistry.SaveFilters[0].Format?.Name);
        Assert.Equal("PCAN Viewer", LogFormatRegistry.SaveFilters[12].Format?.Name);
        Assert.Equal("CANalyzer Binary Log Files", LogFormatRegistry.LoadFilters[15].Format?.Name);
    }
}
