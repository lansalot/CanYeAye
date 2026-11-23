using CanYeAye.Core;
using CanYeAye.Core.FileFormats;
using CanYeAye.Core.Protocols;

namespace CanYeAye.Tests.Protocols;

/// <summary>
/// Keeps the hand-written fixture logs in step with the decoder: every frame in
/// <c>Fixtures/J1939_DM1_examples.log</c> has a documented expected interpretation.
/// </summary>
public class J1939DtcFixtureTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static List<CanFrame> Load(string file)
    {
        var frames = new List<CanFrame>();
        Assert.True(LogFormatRegistry.AutoDetectLoad(Fixture(file), frames), $"failed to load {file}");
        return frames;
    }

    private static string? Describe(CanFrame frame)
    {
        if (!J1939Diagnostics.TryDecode(frame, out var message))
        {
            return null;
        }

        Assert.NotNull(message);
        return J1939Diagnostics.DescribeCompact(message);
    }

    [Fact]
    public void SingleFrameExamplesDecodeAsDocumented()
    {
        var frames = Load("J1939_DM1_examples.log");
        Assert.Equal(11, frames.Count);

        string?[] expected =
        [
            "DM1: no active DTCs",
            "DM1: Amber warning · SPN 100 / FMI 1 (below normal, most severe) · OC 5",
            "DM1: SPN 3226 / FMI 2 (erratic or intermittent) · OC 12 · CM 1",
            "DM1: Red stop · SPN 190 / FMI 5 (open circuit) · OC 3",
            "DM1: Malfunction · SPN 110 / FMI 0 (above normal, most severe) · OC 1",
            "DM1: SPN 516096 / FMI 31 (condition exists) · OC n/a · CM 1 · proprietary",
            "DM2: SPN 157 / FMI 18 (below normal, moderately severe) · OC 7",
            "DM2: no previously active DTCs",
            "DM12: SPN 157 / FMI 18 (below normal, moderately severe) · OC 2",
            null, // EEC1
            null, // CCVS
        ];

        for (var i = 0; i < frames.Count; i++)
        {
            Assert.Equal(expected[i], Describe(frames[i]));
        }
    }

    [Fact]
    public void MultiFrameFixtureDocumentsTheReassemblyGap()
    {
        var frames = Load("J1939_DM1_multiframe_bam.log");
        Assert.Equal(4, frames.Count);

        // The BAM announcement and its data packets are transport-protocol frames, not DTC
        // messages; they only decode after reassembly (not implemented yet).
        var tpFrames = frames
            .Where(frame => J1939Id.Decode(frame.FrameId).Pgn is 0xEC00 or 0xEB00)
            .ToList();
        Assert.Equal(3, tpFrames.Count);
        Assert.All(tpFrames, frame => Assert.False(J1939Diagnostics.TryDecode(frame, out _)));

        // The trailing single-frame DM1 from another ECU still decodes.
        Assert.Equal("DM1: no active DTCs", Describe(frames[^1]));
    }
}
