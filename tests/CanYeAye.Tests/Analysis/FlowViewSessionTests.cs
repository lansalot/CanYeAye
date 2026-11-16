using CanYeAye.Core;
using CanYeAye.Core.Analysis;

namespace CanYeAye.Tests.Analysis;

public class FlowViewSessionTests
{
    private static CanFrame Frame(uint id, long micros, params byte[] data)
    {
        var frame = new CanFrame(id, data);
        frame.Timestamp = FrameTimestamp.FromMicroseconds(micros);
        return frame;
    }

    private static FlowViewSession CreateSession(out CanFrame[] frames)
    {
        frames =
        [
            Frame(0x100, 1_000, 0x01, 0x02, 0x03),
            Frame(0x200, 2_000, 0xFF),
            Frame(0x100, 3_000, 0x01, 0x82, 0x00),
            Frame(0x100, 4_000, 0x00, 0x02, 0x03),
        ];
        return new FlowViewSession(frames);
    }

    [Fact]
    public void ResetCollectsSortedIdsAndSelectsFramesForOneId()
    {
        var session = CreateSession(out _);

        Assert.Equal(new uint[] { 0x100, 0x200 }, session.Ids);
        Assert.Equal(4, session.AllFrames.Count);

        Assert.True(session.Select(0x100));
        Assert.Equal(3, session.FrameCount);
        Assert.Equal(3, session.ByteCount);
        Assert.Equal(0, session.Position);
        Assert.Equal(0x01, session.CurrentBytes[0]);
    }

    [Fact]
    public void MissingIdSelectsAnEmptyFrameSet()
    {
        var session = CreateSession(out _);

        Assert.False(session.Select(0x300));
        Assert.Empty(session.Frames);
        Assert.Equal(0, session.ByteCount);
    }

    [Fact]
    public void SelectingUsesTheFirstFrameAsTheInitialReference()
    {
        var session = CreateSession(out _);
        session.Select(0x100);

        Assert.Equal(session.CurrentBytes, session.ReferenceBytes);
    }

    [Fact]
    public void AutoReferenceUsesPreviousFrameOnEachMove()
    {
        var session = CreateSession(out _);
        session.Select(0x100);
        session.AutoReference = true;

        session.MoveTo(1);

        Assert.Equal(0x01, session.ReferenceBytes[0]);
        Assert.Equal(0x02, session.ReferenceBytes[1]);
        Assert.Equal(0x03, session.ReferenceBytes[2]);
        Assert.Equal(0x01, session.CurrentBytes[0]);
        Assert.Equal(0x82, session.CurrentBytes[1]);
        Assert.Equal(0x00, session.CurrentBytes[2]);
    }

    [Fact]
    public void StaticReferenceStaysPutWhenMoving()
    {
        var session = CreateSession(out _);
        session.Select(0x100);
        session.AutoReference = false;
        session.SetReferenceByte(1, 0xAA);

        session.MoveTo(1);

        Assert.Equal(0xAA, session.ReferenceBytes[1]);
        Assert.Equal(0x82, session.CurrentBytes[1]);
    }

    [Fact]
    public void SeekValueStopsWhenTheCurrentFrameMatches()
    {
        var session = CreateSession(out _);
        session.Select(0x100);
        session.SetSeek(1, 0x82);

        session.MoveTo(0);
        Assert.False(session.SeekHit);

        session.MoveTo(1);
        Assert.True(session.SeekHit);

        session.MoveTo(2);
        Assert.False(session.SeekHit);
    }

    [Fact]
    public void DisabledSeekNeverMatches()
    {
        var session = new FlowViewSession([Frame(0x10, 0, 0x00), Frame(0x10, 1, 0xFF)]);
        session.Select(0x10);

        session.MoveTo(1);

        Assert.Equal(0xFF, session.CurrentBytes[0]);
        Assert.Equal(-1, session.GetSeek(0));
        Assert.False(session.SeekHit);
    }

    [Fact]
    public void StepWrapsOnlyWhenLooping()
    {
        var session = CreateSession(out _);
        session.Select(0x100);

        Assert.False(session.Step(-1, loop: false));
        Assert.False(session.Step(3, loop: false));

        Assert.True(session.Step(-1, loop: true));
        Assert.Equal(2, session.Position);

        Assert.True(session.Step(1, loop: true));
        Assert.Equal(0, session.Position);
    }

    [Fact]
    public void TriggerBitStopsOnlyWhenThatBitChanges()
    {
        var session = CreateSession(out _);
        session.Select(0x100);

        // Bit 7 of byte 1 (0x02 -> 0x82) changes between frames 0 and 1.
        Assert.True(session.ToggleTrigger(8 + 7));
        session.MoveTo(1);
        Assert.True(session.TriggerBitChanged);

        // Frame 1 -> 2 (0x82 -> 0x02) changes that bit back.
        session.MoveTo(2);
        Assert.True(session.TriggerBitChanged);

        // Frame 2 -> 3 leaves byte 1 at 0x02.
        session.MoveTo(3);
        Assert.False(session.TriggerBitChanged);

        Assert.False(session.ToggleTrigger(8 + 7));
        Assert.False(session.IsTrigger(8 + 7));
    }

    [Fact]
    public void AppendAddsNewIdsAndGrowsTheSelectedId()
    {
        var session = CreateSession(out _);
        session.Select(0x100);

        var other = session.Append(Frame(0x300, 5_000, 0x01));
        Assert.True(other.IsNewId);
        Assert.False(other.IsSelectedId);
        Assert.Equal(new uint[] { 0x100, 0x200, 0x300 }, session.Ids);

        var selected = session.Append(Frame(0x100, 6_000, 0x0A, 0x0B, 0x0C, 0x0D));
        Assert.False(selected.IsNewId);
        Assert.True(selected.IsSelectedId);
        Assert.Equal(4, session.FrameCount);
        Assert.Equal(4, session.ByteCount);

        session.MoveTo(session.FrameCount - 1);
        Assert.Equal(0x0D, session.CurrentBytes[3]);
    }

    [Fact]
    public void ResetRebuildsTheIdList()
    {
        var session = CreateSession(out _);
        session.Select(0x100);

        session.Reset([Frame(0x7FF, 0, 0x01)]);

        Assert.Equal(new uint[] { 0x7FF }, session.Ids);
        Assert.Null(session.SelectedId);
        Assert.Empty(session.Frames);
    }
}
