using CanYeAye.Connections.Protocols;
using CanYeAye.Core;

namespace CanYeAye.Tests.Protocols;

public class GvretProtocolTests
{
    private static byte[] ClassicFrame(uint id, byte bus, byte[] data, uint timestamp = 1000, bool extended = false)
    {
        var rawId = id | (extended ? 1u << 31 : 0);
        var bytes = new List<byte> { 0xF1, 0x00 };
        bytes.AddRange(BitConverter.GetBytes(timestamp));
        bytes.AddRange(BitConverter.GetBytes(rawId));
        bytes.Add((byte)((bus << 4) | data.Length));
        bytes.AddRange(data);
        return bytes.ToArray();
    }

    [Fact]
    public void ParsesClassicFrame()
    {
        var protocol = new GvretProtocol();
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        protocol.Feed(ClassicFrame(0x123, 1, [1, 2, 3, 4, 5, 6, 7, 8]));

        var frame = Assert.Single(frames);
        Assert.Equal(0x123U, frame.FrameId);
        Assert.Equal(1, frame.Bus);
        Assert.False(frame.IsExtended);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, frame.Payload);
        Assert.Equal(1000, frame.Timestamp.ToMicroseconds());
        Assert.Equal(FrameType.CanDataFrame, frame.FrameType);
        Assert.True(frame.IsReceived);
    }

    [Fact]
    public void ExtendedFlagComesFromBit31()
    {
        var protocol = new GvretProtocol();
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        protocol.Feed(ClassicFrame(0x18DAF110, 0, [0xAA], extended: true));

        Assert.True(frames[0].IsExtended);
        Assert.Equal(0x18DAF110U, frames[0].FrameId);
    }

    [Fact]
    public void ParsesCanFdFrameWithBitrateSwitch()
    {
        var protocol = new GvretProtocol();
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        var payload = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();
        var bytes = new List<byte> { 0xF1, 0x14 };
        bytes.AddRange(BitConverter.GetBytes(5000u));
        bytes.AddRange(BitConverter.GetBytes(0x321u));
        bytes.Add((byte)(payload.Length | 0x80));
        bytes.Add(1);
        bytes.AddRange(payload);
        bytes.Add(0);
        protocol.Feed(bytes.ToArray());

        var frame = Assert.Single(frames);
        Assert.Equal(FrameType.CanFdDataFrame, frame.FrameType);
        Assert.True(frame.IsFlexibleDataRate);
        Assert.True(frame.HasBitrateSwitch);
        Assert.Equal(payload, frame.Payload);
        Assert.Equal(1, frame.Bus);
    }

    [Fact]
    public void FeedsInOneByteChunks()
    {
        var protocol = new GvretProtocol();
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        foreach (var b in ClassicFrame(0x7AB, 0, [9, 8, 7]))
        {
            protocol.Feed([b]);
        }

        var frame = Assert.Single(frames);
        Assert.Equal(new byte[] { 9, 8, 7 }, frame.Payload);
    }

    [Fact]
    public void DropsFramesOnUnknownBus()
    {
        var protocol = new GvretProtocol { NumBuses = 2 };
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        protocol.Feed(ClassicFrame(1, 3, [1]));

        Assert.Empty(frames);
    }

    [Fact]
    public void InvalidFdLengthResyncsOnNextFrame()
    {
        var protocol = new GvretProtocol();
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        var bad = new List<byte> { 0xF1, 0x14, 1, 0, 0, 0, 1, 0, 0, 0, 65, 0 };
        protocol.Feed(bad.ToArray());
        Assert.Empty(frames);

        protocol.Feed(ClassicFrame(0x55, 0, [1, 2]));
        Assert.Single(frames);
    }

    [Fact]
    public void AppliesTimeBasisToDeviceTimestamp()
    {
        var protocol = new GvretProtocol { TimeBasis = 1_000_000 };
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);

        protocol.Feed(ClassicFrame(1, 0, [1], timestamp: 500));

        Assert.Equal(1_000_500, frames[0].Timestamp.ToMicroseconds());
    }

    [Fact]
    public void EncodesClassicFrameExactly()
    {
        var frame = new CanFrame(0x123, [0xFE, 0x36]);
        frame.Bus = 1;
        var encoded = GvretProtocol.EncodeFrame(frame);

        Assert.Equal(0xF1, encoded[0]);
        Assert.Equal(0x00, encoded[1]);
        Assert.Equal(0x23, encoded[2]);
        Assert.Equal(0x01, encoded[3]);
        Assert.Equal(0x00, encoded[4]);
        Assert.Equal(0x00, encoded[5]);
        Assert.Equal(1, encoded[6]);
        Assert.Equal(2, encoded[7]);
        Assert.Equal(0xFE, encoded[8]);
        Assert.Equal(0x36, encoded[9]);
        Assert.Equal(0x00, encoded[10]);
    }

    [Fact]
    public void EncodesExtendedAndFdFrames()
    {
        var extended = new CanFrame();
        extended.SetFrameId(0x18DAF110);
        extended.SetExtended(true);
        var encoded = GvretProtocol.EncodeFrame(extended);
        Assert.Equal(0xF1, encoded[0]);
        Assert.Equal(0x10, encoded[2]);
        Assert.Equal(0xF1, encoded[3]);
        Assert.Equal(0xDA, encoded[4]);
        Assert.Equal(0x98, encoded[5]);

        var fd = new CanFrame(0x10, new byte[12]);
        fd.SetBitrateSwitch(true);
        var fdEncoded = GvretProtocol.EncodeFrame(fd);
        Assert.Equal(0x14, fdEncoded[1]);
        Assert.Equal(12 | 0x80, fdEncoded[7]);
        Assert.Equal(0, fdEncoded[^1]);
    }

    [Fact]
    public void EncoderUsesDeviceTransmitLayout()
    {
        // RX frames pack len|bus into one byte; the device transmit format uses separate
        // bus and len bytes. This asymmetry is intentional (see gvretserial.cpp).
        var original = new CanFrame(0x456, [1, 2, 3]);
        original.Bus = 2;
        var encoded = GvretProtocol.EncodeFrame(original);

        Assert.Equal(2, encoded[6]);
        Assert.Equal(3, encoded[7]);
        Assert.Equal(new byte[] { 1, 2, 3 }, encoded[8..11]);
    }

    [Fact]
    public void InitSequenceMatchesReference()
    {
        var init = GvretProtocol.EncodeInitSequence();
        Assert.Equal(
            new byte[]
            {
                0xE7, 0xE7, 0xF1, 0x0C, 0xF1, 0x06, 0xF1, 0x07,
                0xF1, 0x16, 0xF1, 0x01, 0xF1, 0x09,
            },
            init);
    }

    [Fact]
    public void ParsesBusParametersReply()
    {
        var protocol = new GvretProtocol();
        GvretBusParameters? parameters = null;
        protocol.BusParametersReceived += (_, p) => parameters = p;

        // can0: enabled, 500000. can1: enabled + single wire, 250000.
        protocol.Feed(
        [
            0xF1, 0x06,
            0x01, 0x20, 0xA1, 0x07, 0x00,
            0x41, 0x90, 0xD0, 0x03, 0x00,
        ]);

        Assert.NotNull(parameters);
        Assert.True(parameters!.Bus0Enabled);
        Assert.False(parameters.Bus0ListenOnly);
        Assert.Equal(500_000u, parameters.Bus0Speed);
        Assert.True(parameters.Bus1Enabled);
        Assert.True(parameters.SingleWire);
        Assert.Equal(250_000u, parameters.Bus1Speed);
    }
}

