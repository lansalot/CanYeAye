using System.Buffers.Binary;
using CanYeAye.Connections.Protocols;
using CanYeAye.Core;

namespace CanYeAye.Tests.Protocols;

public class CanServerProtocolTests
{
    private static byte[] Slot(uint id, int bus, byte[] data)
    {
        var slot = new byte[CanServerProtocol.PacketSize];
        BinaryPrimitives.WriteUInt32LittleEndian(slot, id << 21);
        BinaryPrimitives.WriteUInt32LittleEndian(slot.AsSpan(4), (uint)((bus << 4) | data.Length));
        data.CopyTo(slot, 8);
        return slot;
    }

    [Fact]
    public void DecodesFrameSlot()
    {
        var protocol = new CanServerProtocol { NowMicroseconds = () => 5_000_000 };
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        protocol.Feed(Slot(0x21A, 1, [1, 2, 3, 4, 5, 6, 7, 8]));

        var frame = Assert.Single(frames);
        Assert.Equal(0x21AU, frame.FrameId);
        Assert.Equal(1, frame.Bus);
        Assert.False(frame.IsExtended);
        Assert.Equal(5_000_000, frame.Timestamp.ToMicroseconds());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, frame.Payload);
    }

    [Fact]
    public void SpecialBusMapsToLocalBusTwo()
    {
        var protocol = new CanServerProtocol();
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        protocol.Feed(Slot(0x10, 15, [0xAA]));

        Assert.Equal(2, Assert.Single(frames).Bus);
    }

    [Fact]
    public void DecodesMultipleSlotsAndIgnoresTrailingBytes()
    {
        var protocol = new CanServerProtocol();
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        var first = Slot(0x100, 0, [1]);
        var second = Slot(0x200, 2, [2, 3]);
        protocol.Feed([.. first, .. second, 0xFF]);

        Assert.Equal(2, frames.Count);
        Assert.Equal(0x100U, frames[0].FrameId);
        Assert.Equal(0x200U, frames[1].FrameId);
        Assert.Equal(2, frames[1].Bus);
    }

    [Fact]
    public void LongLengthIsClampedToSlotPayload()
    {
        var protocol = new CanServerProtocol();
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        var slot = Slot(0x123, 0, [1, 2, 3, 4, 5, 6, 7, 8]);
        // Overwrite the DLC nibble with 15.
        BinaryPrimitives.WriteUInt32LittleEndian(slot.AsSpan(4), 15);
        protocol.Feed(slot);

        Assert.Equal(8, Assert.Single(frames).Length);
    }

    [Fact]
    public void SuspendedCaptureIgnoresDatagrams()
    {
        var protocol = new CanServerProtocol { CaptureSuspended = true };
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        protocol.Feed(Slot(0x123, 0, [1]));

        Assert.Empty(frames);
    }
}

public class CanLogServerProtocolTests
{
    private static CanFrame ParseSingle(string line)
    {
        var frame = CanLogServerProtocol.ParseLine(line);
        Assert.NotNull(frame);
        return frame!;
    }

    [Fact]
    public void ParsesClassicFrame()
    {
        var frame = ParseSingle("(166.064992) 0 21A#FE3612FE690507AD");

        Assert.Equal(0x21AU, frame.FrameId);
        Assert.False(frame.IsExtended);
        Assert.Equal(0, frame.Bus);
        Assert.Equal(166_064_992, frame.Timestamp.ToMicroseconds());
        Assert.Equal(new byte[] { 0xFE, 0x36, 0x12, 0xFE, 0x69, 0x05, 0x07, 0xAD }, frame.Payload);
        Assert.True(frame.IsReceived);
    }

    [Fact]
    public void ParsesExtendedFrame()
    {
        var frame = ParseSingle("(1.500000) 0 18DAF110#AABB");

        Assert.Equal(0x18DAF110U, frame.FrameId);
        Assert.True(frame.IsExtended);
        Assert.Equal(1_500_000, frame.Timestamp.ToMicroseconds());
    }

    [Fact]
    public void ParsesCanFdFrameWithFlags()
    {
        var frame = ParseSingle("(2.25) 0 123##1001122");

        Assert.Equal(0x123U, frame.FrameId);
        Assert.Equal(FrameType.CanFdDataFrame, frame.FrameType);
        Assert.True(frame.IsFlexibleDataRate);
        Assert.True(frame.HasBitrateSwitch);
        Assert.False(frame.HasErrorStateIndicator);
        Assert.Equal(new byte[] { 0x00, 0x11, 0x22 }, frame.Payload);
    }

    [Fact]
    public void RejectsLinesWithWrongFieldCount()
    {
        Assert.Null(CanLogServerProtocol.ParseLine("(1.5) 0"));
        Assert.Null(CanLogServerProtocol.ParseLine("(1.5) 0 123#AA extra"));
        Assert.Null(CanLogServerProtocol.ParseLine("(1.5) 0 1234567#AA"));
    }

    [Fact]
    public void FeedSplitsLines()
    {
        var protocol = new CanLogServerProtocol();
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        protocol.Feed("(1.5) 0 123#AA\r\n(2.5) 0 456#BB\n");

        Assert.Equal(2, frames.Count);
        Assert.Equal(0x123U, frames[0].FrameId);
        Assert.Equal(0x456U, frames[1].FrameId);
    }
}

public class MqttFrameCodecTests
{
    [Fact]
    public void RoundTripsExtendedCanFdFrame()
    {
        var frame = new CanFrame();
        frame.SetFrameId(0x18DAF110);
        frame.SetExtended(true);
        frame.SetFrameType(FrameType.CanFdDataFrame);
        frame.SetBitrateSwitch(true);
        frame.SetPayload([0xAA, 0xBB]);

        var payload = MqttFrameCodec.Encode(frame, 1_234_567_890_123);

        var decoded = MqttFrameCodec.Decode(0x18DAF110, payload, false, 0);
        Assert.NotNull(decoded);
        Assert.Equal(0x18DAF110U, decoded!.FrameId);
        Assert.True(decoded.IsExtended);
        Assert.True(decoded.IsFlexibleDataRate);
        Assert.True(decoded.HasBitrateSwitch);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, decoded.Payload);
        Assert.Equal(1_234_567_890_123, decoded.Timestamp.ToMicroseconds());
    }

    [Fact]
    public void DecodesRemoteFrameByPrecedence()
    {
        var payload = new byte[9];
        payload[8] = MqttFrameCodec.FlagRemote;
        var frame = MqttFrameCodec.Decode(0x123, payload, false, 0);

        Assert.Equal(FrameType.RemoteRequestFrame, frame!.FrameType);
    }

    [Fact]
    public void DecodesErrorFrameByPrecedence()
    {
        var payload = new byte[9];
        payload[8] = MqttFrameCodec.FlagError | MqttFrameCodec.FlagRemote;
        var frame = MqttFrameCodec.Decode(0x123, payload, false, 0);

        Assert.Equal(FrameType.ErrorFrame, frame!.FrameType);
    }

    [Fact]
    public void UseSystemTimeOverridesPayloadTimestamp()
    {
        var payload = new byte[9];
        var frame = MqttFrameCodec.Decode(0x123, payload, true, 99_000);

        Assert.Equal(99_000, frame!.Timestamp.ToMicroseconds());
    }

    [Fact]
    public void ShortPayloadIsRejected()
    {
        Assert.Null(MqttFrameCodec.Decode(0x123, new byte[4], false, 0));
    }

    [Theory]
    [InlineData("canyeaye/291", 291u)]
    [InlineData("canyeaye/s/291", 291u)]
    [InlineData("canyeaye/nonsense", null)]
    public void ParsesTopicFrameId(string topic, uint? expected)
    {
        Assert.Equal(expected, MqttFrameCodec.ParseTopicFrameId(topic));
    }

    [Fact]
    public void BuildsTopics()
    {
        Assert.Equal("canyeaye/s/291", MqttFrameCodec.FrameTopic("canyeaye", 291));
        Assert.Equal("canyeaye/+", MqttFrameCodec.SubscriptionTopic("canyeaye"));
    }
}
