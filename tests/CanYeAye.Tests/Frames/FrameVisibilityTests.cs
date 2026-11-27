using CanYeAye.Core;

namespace CanYeAye.Tests.Frames;

/// <summary>
/// Visibility rules used both by the grid and by File → Save Filtered Log. A missing ID or bus
/// entry means "not enabled": filters start closed until the app registers each ID.
/// </summary>
public sealed class FrameVisibilityTests
{
    private static CanFrame Frame(uint id, byte bus = 0, byte value = 0x11)
    {
        var frame = new CanFrame(id, [value]);
        frame.Bus = bus;
        return frame;
    }

    private static Dictionary<uint, bool> Ids(params (uint Id, bool Visible)[] entries) =>
        entries.ToDictionary(entry => entry.Id, entry => entry.Visible);

    private static Dictionary<byte, bool> Buses(params (byte Bus, bool Visible)[] entries) =>
        entries.ToDictionary(entry => entry.Bus, entry => entry.Visible);

    [Fact]
    public void VisibleWhenIdAndBusAreEnabled()
    {
        var frame = Frame(0x100, bus: 1);

        Assert.True(FrameVisibility.IsVisible(
            frame,
            Ids((0x100, true)),
            Buses((1, true))));
    }

    [Fact]
    public void HiddenWhenIdFilterIsUnchecked()
    {
        var frame = Frame(0x100);

        Assert.False(FrameVisibility.IsVisible(
            frame,
            Ids((0x100, false)),
            Buses((0, true))));
    }

    [Fact]
    public void HiddenWhenIdWasNeverRegistered()
    {
        var frame = Frame(0x100);

        Assert.False(FrameVisibility.IsVisible(
            frame,
            Ids((0x200, true)),
            Buses((0, true))));
    }

    [Fact]
    public void HiddenWhenBusFilterIsUnchecked()
    {
        var frame = Frame(0x100, bus: 1);

        Assert.False(FrameVisibility.IsVisible(
            frame,
            Ids((0x100, true)),
            Buses((1, false))));
    }

    [Fact]
    public void SearchMatchesLabelCaseInsensitively()
    {
        var frame = Frame(0x100);

        Assert.True(FrameVisibility.IsVisible(
            frame,
            Ids((0x100, true)),
            Buses((0, true)),
            "engine",
            _ => "0x100 EngineData"));
    }

    [Fact]
    public void SearchMismatchHidesFrame()
    {
        var frame = Frame(0x100);

        Assert.False(FrameVisibility.IsVisible(
            frame,
            Ids((0x100, true)),
            Buses((0, true)),
            "brake",
            _ => "0x100 EngineData"));
    }

    [Fact]
    public void SearchWithoutLabelSelectorHidesFrame()
    {
        var frame = Frame(0x100);

        Assert.False(FrameVisibility.IsVisible(
            frame,
            Ids((0x100, true)),
            Buses((0, true)),
            "engine"));
    }

    [Fact]
    public void SelectVisiblePreservesCaptureOrderAndSkipsHiddenFrames()
    {
        var frames = new[] { Frame(0x100, 0), Frame(0x200, 0), Frame(0x100, 1), Frame(0x300, 0) };

        var visible = FrameVisibility.SelectVisible(
            frames,
            Ids((0x100, true), (0x200, false), (0x300, true)),
            Buses((0, true), (1, false)));

        Assert.Equal([0x100u, 0x300u], visible.Select(frame => frame.FrameId));
    }

    [Fact]
    public void SelectVisibleReturnsEmptyWhenEverythingIsHidden()
    {
        var frames = new[] { Frame(0x100), Frame(0x200) };

        var visible = FrameVisibility.SelectVisible(
            frames,
            Ids((0x100, false), (0x200, false)),
            Buses((0, true)));

        Assert.Empty(visible);
    }

    [Fact]
    public void SelectVisibleWithSearchUsesTheLabelSelector()
    {
        var frames = new[] { Frame(0x100), Frame(0x200) };

        var visible = FrameVisibility.SelectVisible(
            frames,
            Ids((0x100, true), (0x200, true)),
            Buses((0, true)),
            "0x200",
            frame => "0x" + frame.FrameId.ToString("X"));

        Assert.Single(visible);
        Assert.Equal(0x200u, visible[0].FrameId);
    }
}
