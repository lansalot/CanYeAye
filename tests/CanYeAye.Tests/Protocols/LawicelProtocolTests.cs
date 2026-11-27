using System.Text;
using CanYeAye.Connections.Protocols;
using CanYeAye.Core;

namespace CanYeAye.Tests.Protocols;

public class LawicelProtocolTests
{
    private static LawicelProtocol CreateProtocol(long nowMicroseconds = 1_000_000)
    {
        return new LawicelProtocol { NowMicroseconds = () => nowMicroseconds };
    }

    private static List<CanFrame> Feed(LawicelProtocol protocol, string text)
    {
        var frames = new List<CanFrame>();
        protocol.FrameReceived += (_, f) => frames.Add(f);
        protocol.Feed(Encoding.ASCII.GetBytes(text));
        return frames;
    }

    [Fact]
    public void ParsesClassicStandardFrame()
    {
        var protocol = CreateProtocol();
        var frames = Feed(protocol, "t1238FE3612FE690507AD\r");

        var frame = Assert.Single(frames);
        Assert.Equal(0x123U, frame.FrameId);
        Assert.False(frame.IsExtended);
        Assert.True(frame.IsReceived);
        Assert.Equal(FrameType.CanDataFrame, frame.FrameType);
        Assert.Equal(new byte[] { 0xFE, 0x36, 0x12, 0xFE, 0x69, 0x05, 0x07, 0xAD }, frame.Payload);
        Assert.Equal(1_000_000, frame.Timestamp.ToMicroseconds());
    }

    [Fact]
    public void ParsesClassicExtendedFrame()
    {
        var frames = Feed(CreateProtocol(), "T18DAF1102AABB\r");

        var frame = Assert.Single(frames);
        Assert.Equal(0x18DAF110U, frame.FrameId);
        Assert.True(frame.IsExtended);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, frame.Payload);
    }

    [Fact]
    public void ParsesCanFdFrameWithDlcCode()
    {
        var frames = Feed(CreateProtocol(), "d1239" + string.Concat(Enumerable.Range(0, 12).Select(i => i.ToString("X2"))) + "\r");

        var frame = Assert.Single(frames);
        Assert.Equal(0x123U, frame.FrameId);
        Assert.True(frame.IsFlexibleDataRate);
        Assert.False(frame.HasBitrateSwitch);
        Assert.Equal(FrameType.CanFdDataFrame, frame.FrameType);
        Assert.Equal(12, frame.Length);
    }

    [Fact]
    public void ParsesCanFdFrameWithBitrateSwitch()
    {
        var frames = Feed(CreateProtocol(), "b123401020304\r");

        var frame = Assert.Single(frames);
        Assert.True(frame.IsFlexibleDataRate);
        Assert.True(frame.HasBitrateSwitch);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, frame.Payload);
    }

    [Fact]
    public void ParsesExtendedCanFdFrame()
    {
        var frames = Feed(CreateProtocol(), "D18DAF1108AABBCCDD\r");

        var frame = Assert.Single(frames);
        Assert.Equal(0x18DAF110U, frame.FrameId);
        Assert.True(frame.IsExtended);
        Assert.True(frame.IsFlexibleDataRate);
        Assert.False(frame.HasBitrateSwitch);
        Assert.Equal(8, frame.Length);
    }

    [Fact]
    public void HardwareTimestampSuffixReplacesWallClock()
    {
        const long nowUs = 1_700_000_000_000_000;
        var protocol = CreateProtocol(nowUs);

        var frames = Feed(protocol, "t123801020304050607080010\r" + "t12380102030405060708001A\r");

        Assert.Equal(2, frames.Count);
        // The first frame anchors timeBasis to the wall clock minus its 16 ms counter, so it
        // reports exactly "now"; the second is 10 counter ticks later.
        Assert.Equal(1_700_000_000_000_000, frames[0].Timestamp.ToMicroseconds());
        Assert.Equal(1_700_000_000_010_000, frames[1].Timestamp.ToMicroseconds());
    }

    [Fact]
    public void HardwareTimestampWrapsAfterSixtySeconds()
    {
        const long nowUs = 1_700_000_000_000_000;
        var protocol = CreateProtocol(nowUs);

        var frames = Feed(protocol, "t12380102030405060708C350\r" + "t123801020304050607080064\r");

        Assert.Equal(2, frames.Count);
        var delta = frames[1].Timestamp.ToMicroseconds() - frames[0].Timestamp.ToMicroseconds();
        // 50000 ms then 100 ms wraps: 60000 + 100 - 50000 = 10100 ms.
        Assert.Equal(10_100_000, delta);
    }

    [Fact]
    public void UseSystemTimeIgnoresHardwareSuffix()
    {
        const long nowUs = 42_000_000;
        var protocol = CreateProtocol(nowUs);
        protocol.UseSystemTime = true;

        var frames = Feed(protocol, "t123801020304050607080010\r");

        Assert.Equal(nowUs, Assert.Single(frames).Timestamp.ToMicroseconds());
    }

    [Fact]
    public void UnknownCommandIsIgnored()
    {
        var protocol = CreateProtocol();
        var frames = Feed(protocol, "x1238AA\r");

        Assert.Empty(frames);
    }

    [Fact]
    public void SuspendedCaptureDropsFrames()
    {
        var protocol = CreateProtocol();
        protocol.CaptureSuspended = true;

        Assert.Empty(Feed(protocol, "t1231AA\r"));
    }

    [Fact]
    public void EncodesClassicFrame()
    {
        var frame = new CanFrame();
        frame.SetFrameId(0x123);
        frame.SetFrameType(FrameType.CanDataFrame);
        frame.SetPayload(new byte[] { 0xAA, 0xBB });

        var encoded = CreateProtocol().EncodeFrame(frame);

        Assert.Equal("t1232AABB\r", encoded);
    }

    [Fact]
    public void EncodesExtendedFrameWithPaddedId()
    {
        var frame = new CanFrame();
        frame.SetFrameId(0x123);
        frame.SetExtended(true);
        frame.SetFrameType(FrameType.CanDataFrame);
        frame.SetPayload(new byte[] { 0x01 });

        Assert.Equal("T00000123101\r", CreateProtocol().EncodeFrame(frame));
    }

    [Fact]
    public void EncodesCanFdFrameWithDlcCode()
    {
        var frame = new CanFrame();
        frame.SetFrameId(0x456);
        frame.SetFrameType(FrameType.CanFdDataFrame);
        frame.SetPayload(Enumerable.Range(0, 12).Select(i => (byte)i).ToArray());

        var encoded = CreateProtocol().EncodeFrame(frame)!;

        Assert.StartsWith("d4569000102030405060708090A0B\r", encoded);
    }

    [Fact]
    public void EncodesExtendedCanFdFrameWithBitrateSwitch()
    {
        var frame = new CanFrame();
        frame.SetFrameId(0x18DAF110);
        frame.SetFrameType(FrameType.CanFdDataFrame);
        frame.SetBitrateSwitch(true);
        frame.SetPayload(new byte[] { 0xFF });

        Assert.Equal("B18DAF1101FF\r", CreateProtocol().EncodeFrame(frame));
    }

    [Fact]
    public void ErrorFrameIsNotSent()
    {
        var frame = new CanFrame();
        frame.SetFrameType(FrameType.ErrorFrame);

        Assert.Null(CreateProtocol().EncodeFrame(frame));
    }

    [Theory]
    [InlineData(10_000u, '0')]
    [InlineData(20_000u, '1')]
    [InlineData(50_000u, '2')]
    [InlineData(100_000u, '3')]
    [InlineData(125_000u, '4')]
    [InlineData(250_000u, '5')]
    [InlineData(500_000u, '6')]
    [InlineData(800_000u, '7')]
    [InlineData(1_000_000u, '8')]
    [InlineData(83_333u, '6')]
    public void EncodesBusSpeedCode(uint bitrate, char expected)
    {
        Assert.Equal(expected, LawicelProtocol.EncodeBusSpeed(bitrate));
    }

    [Theory]
    [InlineData(1_000_000u, "Y1")]
    [InlineData(2_000_000u, "Y2")]
    [InlineData(4_000_000u, "Y4")]
    [InlineData(5_000_000u, "Y5")]
    [InlineData(3_000_000u, "Y2")]
    public void EncodesDataRateCode(uint bitrate, string expected)
    {
        Assert.Equal(expected, LawicelProtocol.EncodeDataRate(bitrate));
    }

    [Fact]
    public void BuildsOpenSequence()
    {
        Assert.Equal("C\rS6\rO\r", LawicelProtocol.BuildOpenSequence(500_000, false, 0));
        Assert.Equal("C\rS8\rY5\rO\r", LawicelProtocol.BuildOpenSequence(1_000_000, true, 5_000_000));
    }

    [Fact]
    public void BuildsListenOnlyOpenSequence()
    {
        Assert.Equal("C\rS6\rL\r", LawicelProtocol.BuildOpenSequence(500_000, false, 0, listenOnly: true));
        Assert.Equal(
            "C\rS8\rY5\rL\r",
            LawicelProtocol.BuildOpenSequence(1_000_000, true, 5_000_000, listenOnly: true));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 8)]
    [InlineData(12, 9)]
    [InlineData(16, 10)]
    [InlineData(20, 11)]
    [InlineData(24, 12)]
    [InlineData(32, 13)]
    [InlineData(48, 14)]
    [InlineData(64, 15)]
    public void DlcCodeTablesAgree(int bytes, int code)
    {
        Assert.Equal((byte)code, LawicelProtocol.BytesToDlcCode(bytes));
        Assert.Equal(bytes, LawicelProtocol.DlcCodeToBytes(code));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(65)]
    public void UnrepresentableLengthsUseCodeZero(int bytes)
    {
        Assert.Equal(0, LawicelProtocol.BytesToDlcCode(bytes));
    }
}

