using CanYeAye.Connections.Protocols;
using CanYeAye.Core;

namespace CanYeAye.Tests.Protocols;

public class SocketCanDProtocolTests
{
    private static SocketCanDProtocol CreateRawProtocol(out List<string> commands)
    {
        var protocol = new SocketCanDProtocol(2, "can0");
        var sent = new List<string>();
        commands = sent;
        protocol.SendRequested += (_, text) => sent.Add(text);
        protocol.Feed("< hi >");
        protocol.Feed("< ok >");
        protocol.Feed("< ok >");
        return protocol;
    }

    [Fact]
    public void HandshakeSendsOpenThenRawMode()
    {
        var protocol = new SocketCanDProtocol(0, "can0");
        var sent = new List<string>();
        protocol.SendRequested += (_, text) => sent.Add(text);

        protocol.Feed("< hi >");
        Assert.Equal(SocketCanDState.Bcm, protocol.State);
        protocol.Feed("< ok >");
        Assert.Equal(SocketCanDState.SwitchingToRaw, protocol.State);
        protocol.Feed("< ok >");
        Assert.Equal(SocketCanDState.RawMode, protocol.State);

        Assert.Equal(["< open can0 >", "< rawmode >"], sent);
        Assert.True(protocol.CanSend);
    }

    [Fact]
    public void IgnoresUnexpectedGreeting()
    {
        var protocol = new SocketCanDProtocol(0, "can0");
        var sent = new List<string>();
        protocol.SendRequested += (_, text) => sent.Add(text);

        protocol.Feed("< hello >");

        Assert.Equal(SocketCanDState.Idle, protocol.State);
        Assert.Empty(sent);
    }

    [Fact]
    public void DecodesExtendedFrame()
    {
        var protocol = CreateRawProtocol(out _);
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        protocol.Feed("< frame 1A2B 1234567.890123 0102030405060708 >");

        var frame = Assert.Single(frames);
        Assert.Equal(0x1A2BU, frame.FrameId);
        Assert.True(frame.IsExtended);
        Assert.Equal(2, frame.Bus);
        Assert.Equal(1_234_567_890_123L, frame.Timestamp.ToMicroseconds());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, frame.Payload);
        Assert.Equal(FrameType.CanDataFrame, frame.FrameType);
        Assert.True(frame.IsReceived);
    }

    [Fact]
    public void DecodesStandardFrame()
    {
        var protocol = CreateRawProtocol(out _);
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        protocol.Feed("< frame 123 1.5 0102 >");

        var frame = Assert.Single(frames);
        Assert.Equal(0x123U, frame.FrameId);
        Assert.False(frame.IsExtended);
        Assert.Equal(1_500_000, frame.Timestamp.ToMicroseconds());
        Assert.Equal(new byte[] { 1, 2 }, frame.Payload);
    }

    [Fact]
    public void DecodesMultipleFramesFromOneDatagram()
    {
        var protocol = CreateRawProtocol(out _);
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        protocol.Feed("< frame 123 1.5 0102 >< frame 456 2.5 0304 >");

        Assert.Equal(2, frames.Count);
        Assert.Equal(0x123U, frames[0].FrameId);
        Assert.Equal(0x456U, frames[1].FrameId);
        Assert.Equal(2_500_000, frames[1].Timestamp.ToMicroseconds());
    }

    [Fact]
    public void BuffersIncompleteFrame()
    {
        var protocol = CreateRawProtocol(out _);
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        protocol.Feed("< frame 123 ");
        Assert.Empty(frames);

        protocol.Feed("1.5 0102 >");
        var frame = Assert.Single(frames);
        Assert.Equal(new byte[] { 1, 2 }, frame.Payload);
    }

    [Fact]
    public void FrameWithoutDataHasZeroLength()
    {
        var protocol = CreateRawProtocol(out _);
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        protocol.Feed("< frame 123 1.5 >");

        Assert.Equal(0, Assert.Single(frames).Length);
    }

    [Fact]
    public void CompoundOkDatagramSwitchesToRawAndDecodes()
    {
        var protocol = new SocketCanDProtocol(0, "can0");
        protocol.SendRequested += (_, _) => { };
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        protocol.Feed("< hi >");
        protocol.Feed("< ok >");
        protocol.Feed("< ok >< frame 123 1.5 0102 >");

        Assert.Equal(SocketCanDState.RawMode, protocol.State);
        Assert.Equal(0x123U, Assert.Single(frames).FrameId);
    }

    [Fact]
    public void SuspendedCaptureDropsFrames()
    {
        var protocol = CreateRawProtocol(out _);
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);
        protocol.CaptureSuspended = true;

        protocol.Feed("< frame 123 1.5 0102 >");

        Assert.Empty(frames);
    }

    [Fact]
    public void EncodesSendCommand()
    {
        var protocol = CreateRawProtocol(out _);
        var frame = new CanFrame();
        frame.SetFrameId(0x123);
        frame.SetFrameType(FrameType.CanDataFrame);
        frame.SetPayload(new byte[] { 0xAA, 0x01 });

        Assert.Equal("< send 123 2 aa 1 >", protocol.EncodeFrame(frame));
    }

    [Fact]
    public void EncodesExtendedIdWithBit31()
    {
        var protocol = CreateRawProtocol(out _);
        var frame = new CanFrame();
        frame.SetFrameId(0x18DAF110);
        frame.SetExtended(true);
        frame.SetFrameType(FrameType.CanDataFrame);
        frame.SetPayload(new byte[] { 0xFF });

        Assert.Equal("< send 98daf110 1 ff >", protocol.EncodeFrame(frame));
    }

    [Fact]
    public void EncodeRequiresRawMode()
    {
        var protocol = new SocketCanDProtocol(0, "can0");
        var frame = new CanFrame();
        frame.SetFrameId(0x123);
        frame.SetPayload(new byte[] { 1 });

        Assert.Null(protocol.EncodeFrame(frame));
    }

    [Fact]
    public void ErrorFrameIsNotSent()
    {
        var protocol = CreateRawProtocol(out _);
        var frame = new CanFrame();
        frame.SetFrameType(FrameType.ErrorFrame);

        Assert.Null(protocol.EncodeFrame(frame));
    }

    [Fact]
    public void GarbageWithoutFrameMarkerIsDiscarded()
    {
        var protocol = CreateRawProtocol(out _);
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        protocol.Feed("this is not a frame at all");
        protocol.Feed("< frame 123 1.5 0102 >");

        Assert.Equal(0x123U, Assert.Single(frames).FrameId);
    }
}
