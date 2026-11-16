using CanYeAye.Core;
using CanYeAye.Core.FileFormats;

namespace CanYeAye.Tests.FileFormats;

/// <summary>
/// Save/load round trips. Each format keeps a different subset of the frame model, so the
/// assertions state exactly what a format must preserve rather than demanding byte equality.
/// </summary>
public class RoundTripTests
{
    private static string TempFile(string extension) =>
        Path.Combine(Path.GetTempPath(), $"canyeaye-rt-{Guid.NewGuid():N}{extension}");

    private static CanFrame Classic(uint id, byte bus, long micros, bool received, params byte[] data)
    {
        var frame = new CanFrame();
        frame.SetFrameId(id);
        frame.Bus = bus;
        frame.Timestamp = FrameTimestamp.FromMicroseconds(micros);
        frame.SetFrameType(FrameType.CanDataFrame);
        frame.SetReceived(received);
        frame.SetPayload(data);
        return frame;
    }

    private static CanFrame Extended(uint id, byte bus, long micros, bool received, params byte[] data)
    {
        var frame = Classic(id, bus, micros, received, data);
        frame.SetExtended(true);
        return frame;
    }

    private static CanFrame Remote(uint id, byte bus, long micros, int length)
    {
        var frame = new CanFrame();
        frame.SetFrameId(id);
        frame.Bus = bus;
        frame.Timestamp = FrameTimestamp.FromMicroseconds(micros);
        frame.SetFrameType(FrameType.RemoteRequestFrame);
        frame.SetPayload(new byte[length]);
        return frame;
    }

    private static CanFrame Fd(uint id, byte bus, long micros, bool brs, bool esi, params byte[] data)
    {
        var frame = new CanFrame();
        frame.SetFrameId(id);
        frame.Bus = bus;
        frame.Timestamp = FrameTimestamp.FromMicroseconds(micros);
        frame.SetFrameType(FrameType.CanFdDataFrame);
        frame.SetBitrateSwitch(brs);
        frame.SetErrorStateIndicator(esi);
        frame.SetPayload(data);
        return frame;
    }

    private static List<CanFrame> SaveAndLoad(ILogFormat format, IReadOnlyList<CanFrame> frames)
    {
        var path = TempFile(format.DefaultExtension);
        try
        {
            Assert.True(format.Save(path, frames), $"{format.Name} save failed");
            var loaded = new List<CanFrame>();
            Assert.True(format.Load(path, loaded), $"{format.Name} load failed");
            return loaded;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertSameFrames(IReadOnlyList<CanFrame> expected, IReadOnlyList<CanFrame> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].FrameId, actual[i].FrameId);
            Assert.Equal(expected[i].IsExtended, actual[i].IsExtended);
            Assert.Equal(expected[i].Payload, actual[i].Payload);
            Assert.Equal(expected[i].Timestamp.ToMicroseconds(), actual[i].Timestamp.ToMicroseconds());
        }
    }

    [Fact]
    public void NativeCsvKeepsEverything()
    {
        var frames = new List<CanFrame>
        {
            Classic(0x21A, 0, 166_064_000, true, 0xFE, 0x36, 0x12, 0xFE, 0x69, 0x05, 0x07, 0xAD),
            Extended(0x18FF50E5, 1, 166_064_500, false, 0x01, 0x02, 0x03),
            Fd(0x456, 3, 166_065_500, true, false, Enumerable.Range(0, 16).Select(v => (byte)v).ToArray()),
            Fd(0x789, 0, 166_066_000, false, true, Enumerable.Range(0, 64).Select(v => (byte)v).ToArray()),
        };

        var loaded = SaveAndLoad(LogFormatRegistry.NativeCsv, frames);

        // Remote frames are not representable in the native CSV columns, so they are not tested.
        Assert.Equal(4, loaded.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            Assert.Equal(frames[i].FrameId, loaded[i].FrameId);
            Assert.Equal(frames[i].IsExtended, loaded[i].IsExtended);
            Assert.Equal(frames[i].Bus, loaded[i].Bus);
            Assert.Equal(frames[i].IsReceived, loaded[i].IsReceived);
            Assert.Equal(frames[i].Payload, loaded[i].Payload);
            Assert.Equal(frames[i].Timestamp.ToMicroseconds(), loaded[i].Timestamp.ToMicroseconds());
            Assert.Equal(frames[i].FrameType, loaded[i].FrameType);
            Assert.Equal(frames[i].HasBitrateSwitch, loaded[i].HasBitrateSwitch);
            Assert.Equal(frames[i].HasErrorStateIndicator, loaded[i].HasErrorStateIndicator);
        }
    }

    [Fact]
    public void CandumpKeepsIdsPayloadsAndTimestamps()
    {
        var frames = new List<CanFrame>
        {
            Classic(0x21A, 0, 166_064_000, true, 0xFE, 0x36, 0x12, 0xFE, 0x69, 0x05, 0x07, 0xAD),
            Extended(0x18FF50E5, 0, 166_064_500, true, 0x01, 0x02, 0x03),
            Remote(0x321, 0, 166_065_000, 4),
            Fd(0x456, 0, 166_065_500, true, true, Enumerable.Range(0, 12).Select(v => (byte)v).ToArray()),
        };

        var loaded = SaveAndLoad(LogFormatRegistry.Candump, frames);

        Assert.Equal(4, loaded.Count);
        AssertSameFrames(frames, loaded);
        Assert.Equal(FrameType.RemoteRequestFrame, loaded[2].FrameType);
        Assert.True(loaded[3].IsFlexibleDataRate);
        Assert.True(loaded[3].HasBitrateSwitch);
        Assert.True(loaded[3].HasErrorStateIndicator);
    }

    [Fact]
    public void CrtdKeepsIdsPayloadsBusesAndDirection()
    {
        var frames = new List<CanFrame>
        {
            Classic(0x21A, 0, 166_064_000, true, 0xFE, 0x36, 0x12, 0xFE, 0x69, 0x05, 0x07, 0xAD),
            Extended(0x18FF50E5, 1, 166_064_500, false, 0x01, 0x02, 0x03),
            Classic(0x7FF, 3, 166_065_000, true, 0xAA),
        };

        var loaded = SaveAndLoad(LogFormatRegistry.Crtd, frames);

        AssertSameFrames(frames, loaded);
        Assert.Equal([true, false, true], loaded.Select(f => f.IsReceived).ToArray());
        Assert.Equal([0, 1, 3], loaded.Select(f => (int)f.Bus).ToArray());
    }

    [Fact]
    public void MicrochipKeepsIdsPayloadsAndTimestamps()
    {
        var frames = new List<CanFrame>
        {
            Classic(0x21A, 0, 166_064_000, true, 0xFE, 0x36, 0x12, 0xFE, 0x69, 0x05, 0x07, 0xAD),
            Extended(0x18FF50E5, 0, 166_065_000, false, 0x01, 0x02),
        };

        var loaded = SaveAndLoad(LogFormatRegistry.Microchip, frames);

        Assert.Equal(2, loaded.Count);
        AssertSameFrames(frames, loaded);
        Assert.True(loaded[0].IsReceived);
        Assert.False(loaded[1].IsReceived);
        Assert.True(loaded[1].IsExtended);
    }

    [Fact]
    public void VectorTraceKeepsIdsPayloadsAndHundredMicrosecondTicks()
    {
        var frames = new List<CanFrame>
        {
            Classic(0x21A, 0, 1_234_500, true, 0xFE, 0x36, 0x12, 0xFE, 0x69, 0x05, 0x07, 0xAD),
            Extended(0x18FF50E5, 0, 2_000_000, true, 0x01, 0x02, 0x03),
        };

        var loaded = SaveAndLoad(LogFormatRegistry.VectorTrace, frames);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(0x21AU, loaded[0].FrameId);
        Assert.Equal(frames[0].Payload, loaded[0].Payload);
        Assert.Equal(1_234_500, loaded[0].Timestamp.ToMicroseconds());
        Assert.Equal(0x18FF50E5U, loaded[1].FrameId);
        Assert.True(loaded[1].IsExtended);
    }

    [Fact]
    public void IxxatMiniLogKeepsIdsPayloadsAndFormat()
    {
        var frames = new List<CanFrame>
        {
            Classic(0x21A, 0, 1_500_000, true, 0xFE, 0x36, 0x12, 0xFE),
            Extended(0x18FF50E5, 0, 2_500_000, true, 0x01, 0x02, 0x03),
        };

        var loaded = SaveAndLoad(LogFormatRegistry.IxxatMiniLog, frames);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(0x21AU, loaded[0].FrameId);
        Assert.False(loaded[0].IsExtended);
        Assert.Equal(frames[0].Payload, loaded[0].Payload);
        Assert.Equal(1_500_000, loaded[0].Timestamp.ToMicroseconds());
        Assert.Equal(0x18FF50E5U, loaded[1].FrameId);
        Assert.True(loaded[1].IsExtended);
    }

    [Fact]
    public void CanDoKeepsClassicIdsPayloadsAndMilliseconds()
    {
        var frames = new List<CanFrame>
        {
            Classic(0x21A, 0, 65_000, true, 0xFE, 0x36, 0x12, 0xFE, 0x69, 0x05, 0x07, 0xAD),
            Classic(0x123, 0, 125_000, true, 0x01, 0x02),
        };

        var loaded = SaveAndLoad(LogFormatRegistry.CanDo, frames);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(0x21AU, loaded[0].FrameId);
        Assert.Equal(frames[0].Payload, loaded[0].Payload);
        Assert.Equal(65_000, loaded[0].Timestamp.ToMicroseconds());
        Assert.Equal(125_000, loaded[1].Timestamp.ToMicroseconds());
    }

    [Fact]
    public void PcanKeepsIdsPayloadsAndRelativeTimes()
    {
        var frames = new List<CanFrame>
        {
            Classic(0x21A, 0, 1_000_000, true, 0xFE, 0x36, 0x12, 0xFE),
            Classic(0x22A, 2, 1_500_000, false, 0x01),
            Extended(0x18FF50E5, 1, 2_000_000, true, 0x01, 0x02, 0x03),
        };

        var loaded = SaveAndLoad(LogFormatRegistry.Pcan, frames);

        Assert.Equal(3, loaded.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            Assert.Equal(frames[i].FrameId, loaded[i].FrameId);
            Assert.Equal(frames[i].IsExtended, loaded[i].IsExtended);
            Assert.Equal(frames[i].Payload, loaded[i].Payload);
            Assert.Equal(frames[i].Bus, loaded[i].Bus);
            Assert.Equal(
                frames[i].Timestamp.ToMicroseconds() - frames[0].Timestamp.ToMicroseconds(),
                loaded[i].Timestamp.ToMicroseconds() - loaded[0].Timestamp.ToMicroseconds());
        }
    }

    [Fact]
    public void CanalyzerAscKeepsIdsPayloadsBusesAndDirections()
    {
        var frames = new List<CanFrame>
        {
            Classic(0x21A, 0, 1_000_000, true, 0xFE, 0x36, 0x12, 0xFE),
            Classic(0x22A, 2, 1_500_000, false, 0x01),
            Extended(0x18FF50E5, 1, 2_000_000, true, 0x01, 0x02, 0x03),
            Fd(0x456, 0, 2_500_000, true, false, Enumerable.Range(0, 12).Select(v => (byte)v).ToArray()),
        };

        var loaded = SaveAndLoad(LogFormatRegistry.CanalyzerAsc, frames);

        Assert.Equal(4, loaded.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            Assert.Equal(frames[i].FrameId, loaded[i].FrameId);
            Assert.Equal(frames[i].Bus, loaded[i].Bus);
            Assert.Equal(frames[i].Payload, loaded[i].Payload);
            Assert.Equal(
                frames[i].Timestamp.ToMicroseconds() - frames[0].Timestamp.ToMicroseconds(),
                loaded[i].Timestamp.ToMicroseconds() - loaded[0].Timestamp.ToMicroseconds());
            Assert.Equal(frames[i].IsReceived, loaded[i].IsReceived);
        }

        Assert.True(loaded[2].IsExtended);
        Assert.True(loaded[3].IsFlexibleDataRate);
        Assert.True(loaded[3].HasBitrateSwitch);
    }

    [Fact]
    public void CabanaKeepsIdsPayloadsAndBuses()
    {
        var frames = new List<CanFrame>
        {
            Classic(0x21A, 0, 10_000_000, true, 0xFE, 0x36, 0x12, 0xFE),
            Classic(0x7FF, 3, 10_500_000, true, 0x01, 0x02),
        };

        var loaded = SaveAndLoad(LogFormatRegistry.Cabana, frames);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(0x21AU, loaded[0].FrameId);
        Assert.Equal(0, loaded[0].Bus);
        Assert.Equal(frames[0].Payload, loaded[0].Payload);
        Assert.Equal(0, loaded[0].Timestamp.ToMicroseconds()); // rebased to the first frame
        Assert.Equal(0x7FFU, loaded[1].FrameId);
        Assert.Equal(3, loaded[1].Bus);
        Assert.Equal(500_000, loaded[1].Timestamp.ToMicroseconds());
    }

    [Fact]
    public void GenericCsvKeepsIdsAndPayloads()
    {
        var frames = new List<CanFrame>
        {
            Classic(0x21A, 0, 1_000_000, true, 0xFE, 0x36, 0x12, 0xFE),
            Extended(0x18FF50E5, 0, 2_000_000, true, 0x01, 0x02, 0x03),
        };

        var loaded = SaveAndLoad(LogFormatRegistry.GenericCsv, frames);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(0x21AU, loaded[0].FrameId);
        Assert.Equal(frames[0].Payload, loaded[0].Payload);
        Assert.Equal(0x18FF50E5U, loaded[1].FrameId);
        Assert.True(loaded[1].IsExtended);
        Assert.Equal(5000, loaded[1].Timestamp.ToMicroseconds() - loaded[0].Timestamp.ToMicroseconds());
    }

    [Fact]
    public void CarBusKeepsIdsPayloadsAndBuses()
    {
        var frames = new List<CanFrame>
        {
            Classic(0x45A, 1, 46_016_000, true, 0x5A, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00),
            Classic(0x7FF, 2, 46_500_000, true, 0x24),
            Extended(0x18FF50E5, 1, 47_000_000, true, 0x01, 0x02, 0x03),
        };

        var loaded = SaveAndLoad(LogFormatRegistry.CarBus, frames);

        Assert.Equal(3, loaded.Count);
        Assert.Equal(0x45AU, loaded[0].FrameId);
        Assert.Equal(1, loaded[0].Bus);
        Assert.Equal(frames[0].Payload, loaded[0].Payload);
        Assert.Equal(0x7FFU, loaded[1].FrameId);
        Assert.Equal(2, loaded[1].Bus);
        Assert.Equal(0x18FF50E5U, loaded[2].FrameId);
        Assert.True(loaded[2].IsExtended);
        Assert.Equal(1, loaded[2].Bus);
    }

    [Fact]
    public void BusMasterKeepsIdsPayloadsAndRelativeTimes()
    {
        var frames = new List<CanFrame>
        {
            Classic(0x21A, 0, 80_000_000_000, true, 0xFE, 0x36, 0x12, 0xFE),
            Classic(0x22A, 2, 80_001_500_000, false, 0x01),
            Extended(0x18FF50E5, 1, 80_002_000_000, true, 0x01, 0x02, 0x03),
            Remote(0x321, 0, 80_003_000_000, 4),
        };

        var loaded = SaveAndLoad(LogFormatRegistry.BusMaster, frames);

        Assert.Equal(4, loaded.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            Assert.Equal(frames[i].FrameId, loaded[i].FrameId);
            Assert.Equal(frames[i].IsExtended, loaded[i].IsExtended);
            Assert.Equal(frames[i].Bus, loaded[i].Bus);
            Assert.Equal(frames[i].Payload, loaded[i].Payload);
            Assert.Equal(
                frames[i].Timestamp.ToMicroseconds() - frames[0].Timestamp.ToMicroseconds(),
                loaded[i].Timestamp.ToMicroseconds() - loaded[0].Timestamp.ToMicroseconds());
        }

        Assert.Equal(FrameType.RemoteRequestFrame, loaded[3].FrameType);
    }

    [Fact]
    public void RegistrySaveUsesFilterIndex()
    {
        var path = TempFile(".csv");
        try
        {
            var frames = new List<CanFrame> { Classic(0x123, 0, 1_000_000, true, 0x01, 0x02) };
            Assert.True(LogFormatRegistry.Save(path, 0, frames));
            Assert.StartsWith(NativeCsvFormat.V2Header, File.ReadAllText(path));

            Assert.False(LogFormatRegistry.Save(path, 99, frames));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
