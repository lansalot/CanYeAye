using WinCAN.Core;
using WinCAN.Core.Sniffing;

namespace WinCAN.Tests.Sniffing;

public class SnifferEntryTests
{
    private static CanFrame Frame(uint id, long timestampUs, params byte[] data)
    {
        var frame = new CanFrame();
        frame.SetFrameId(id);
        frame.SetPayload(data);
        frame.Timestamp = FrameTimestamp.FromMicroseconds(timestampUs);
        return frame;
    }

    [Fact]
    public void TracksDeltaFromFrameTimestamps()
    {
        var entry = new SnifferEntry(Frame(0x100, 1_000_000, 1, 2), nowMs: 0, sequence: 0);

        entry.Update(Frame(0x100, 1_100_000, 1, 3), nowMs: 100, sequence: 1, muteNotched: false);

        Assert.Equal(0x100U, entry.Id);
        Assert.Equal(100_000, entry.DeltaMicroseconds);
        Assert.Equal(2, entry.FrameCount);
        Assert.Equal(new[] { 1, 3 }, new[] { entry.Data(0), entry.Data(1) });
    }

    [Fact]
    public void RateUsesArrivalsWithinTheLastSecond()
    {
        var entry = new SnifferEntry(Frame(0x100, 0, 1), nowMs: 0, sequence: 0);
        for (var i = 1; i <= 9; i++)
        {
            entry.Update(Frame(0x100, i * 100_000, 1), nowMs: i * 100, sequence: (uint)i, muteNotched: false);
        }

        // Ten arrivals over 900 ms => ~11.1 Hz.
        Assert.Equal(10 * 1000.0 / 900.0, entry.RateHz(900), 3);
        // 1.5 s later only the last arrival is in the window.
        Assert.Equal(1, entry.RateHz(1800), 3);
    }

    [Fact]
    public void ChangedBytesAreColouredOnlyAfterTheMarkerWindowCloses()
    {
        var entry = new SnifferEntry(Frame(0x200, 0, 0x10, 0x20), nowMs: 0, sequence: 0);
        entry.Update(Frame(0x200, 1000, 0x11, 0x1F), nowMs: 10, sequence: 1, muteNotched: false);

        // Marker bits are only considered once the notch window rotates.
        Assert.Equal(SnifferByteChange.None, entry.ChangeFor(0));
        entry.RotateMarker();

        Assert.Equal(SnifferByteChange.Increased, entry.ChangeFor(0));
        Assert.Equal(SnifferByteChange.Decreased, entry.ChangeFor(1));
    }

    [Fact]
    public void NotchIgnoresBitsThatChangedInTheLastWindow()
    {
        var entry = new SnifferEntry(Frame(0x300, 0, 0x00), nowMs: 0, sequence: 0);
        entry.Update(Frame(0x300, 1000, 0x01), nowMs: 10, sequence: 1, muteNotched: false);
        entry.RotateMarker();
        Assert.Equal(SnifferByteChange.Increased, entry.ChangeFor(0));

        entry.Notch();
        entry.Update(Frame(0x300, 2000, 0x00), nowMs: 20, sequence: 2, muteNotched: false);
        entry.RotateMarker();

        // The only changing bit is notched, so no colour is reported.
        Assert.Equal(SnifferByteChange.None, entry.ChangeFor(0));

        entry.Unnotch();
        entry.Update(Frame(0x300, 3000, 0x01), nowMs: 30, sequence: 3, muteNotched: false);
        entry.RotateMarker();
        Assert.Equal(SnifferByteChange.Increased, entry.ChangeFor(0));
    }

    [Fact]
    public void MuteNotchedBitsKeepsTheDisplayedValue()
    {
        var entry = new SnifferEntry(Frame(0x400, 0, 0x00, 0x55), nowMs: 0, sequence: 0);
        entry.Update(Frame(0x400, 1000, 0x01, 0x55), nowMs: 10, sequence: 1, muteNotched: false);
        entry.RotateMarker();
        entry.Notch(); // notches bit 0 of byte 0

        entry.Update(Frame(0x400, 2000, 0x00, 0x56), nowMs: 20, sequence: 2, muteNotched: true);

        // The notched bit change is hidden, the unmasked byte still updates.
        Assert.Equal(0x01, entry.Data(0));
        Assert.Equal(0x56, entry.Data(1));
    }

    [Fact]
    public void MinimumRateHidesAnIdThatIsTooSlow()
    {
        var entry = new SnifferEntry(Frame(0x600, 0, 1), nowMs: 0, sequence: 0);
        for (var i = 1; i <= 49; i++)
        {
            entry.Update(Frame(0x600, i * 20_000, 1), nowMs: i * 20, sequence: (uint)i, muteNotched: false);
        }

        // ~50 Hz over the first second: above 10 Hz, below 1000 Hz.
        Assert.False(entry.IsRateBelow(10, 1000));
        Assert.True(entry.IsRateBelow(1000, 1000));

        // Under a second of history the rate is never judged.
        Assert.False(entry.IsRateBelow(1000, 500));

        // A disabled filter never hides anything.
        Assert.False(entry.IsRateBelow(0, 5000));
    }

    [Fact]
    public void FadeGrowsAsBytesStayUnchanged()
    {
        var entry = new SnifferEntry(Frame(0x500, 0, 0xAA), nowMs: 0, sequence: 0);

        Assert.Equal(0, entry.FadeFactor(0, 0, 200));
        Assert.True(entry.FadeFactor(0, 5, 200) > 0);
        Assert.True(entry.FadeFactor(0, 50, 200) <= 225.0 / 255.0);
    }
}
