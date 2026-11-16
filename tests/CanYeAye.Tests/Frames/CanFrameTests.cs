using CanYeAye.Core;

namespace CanYeAye.Tests.Frames;

public class CanFrameTests
{
    [Fact]
    public void Timestamp_RoundTripsMicroseconds()
    {
        var ts = FrameTimestamp.FromMicroseconds(1_234_567);
        Assert.Equal(1, ts.Seconds);
        Assert.Equal(234_567, ts.Microseconds);
        Assert.Equal(1_234_567, ts.ToMicroseconds());
    }

    [Fact]
    public void Timestamp_OrdersByTotalMicroseconds()
    {
        var a = new FrameTimestamp(1, 999_999);
        var b = new FrameTimestamp(2, 0);
        Assert.True(a < b);
        Assert.False(b < a);
    }

    [Fact]
    public void SetFrameId_StandardIdDoesNotExtend()
    {
        var frame = new CanFrame();
        frame.SetFrameId(0x7FF);
        Assert.Equal(0x7FFU, frame.FrameId);
        Assert.False(frame.IsExtended);
        Assert.True(frame.IsValidFrameId);
    }

    [Fact]
    public void SetFrameId_Above11BitsSetsExtended()
    {
        var frame = new CanFrame();
        frame.SetFrameId(0x18DAF110);
        Assert.Equal(0x18DAF110U, frame.FrameId);
        Assert.True(frame.IsExtended);
    }

    [Fact]
    public void SetFrameId_TooLargeIsInvalid()
    {
        var frame = new CanFrame();
        frame.SetFrameId(0x20000000);
        Assert.Equal(0U, frame.FrameId);
        Assert.False(frame.IsValidFrameId);
        Assert.False(frame.IsValid());
    }

    [Fact]
    public void PayloadLongerThanEightBytesBecomesCanFd()
    {
        var frame = new CanFrame();
        frame.SetPayload([1, 2, 3, 4, 5, 6, 7, 8, 9]);
        Assert.Equal(FrameType.CanFdDataFrame, frame.FrameType);
        Assert.True(frame.IsFlexibleDataRate);
        Assert.False(frame.IsValid()); // 9 bytes is not a legal FD DLC
    }

    [Fact]
    public void CanFdPayloadMustBeLegalDlc()
    {
        var frame = new CanFrame();
        frame.SetPayload(new byte[9]);
        Assert.False(frame.IsValid());
        frame.SetPayload(new byte[12]);
        Assert.True(frame.IsValid());
    }

    [Fact]
    public void BitrateSwitchAndEsiRequireCanFd()
    {
        var frame = new CanFrame();
        frame.SetBitrateSwitch(true);
        Assert.Equal(FrameType.CanFdDataFrame, frame.FrameType);
        Assert.True(frame.HasBitrateSwitch);

        frame.SetFrameType(FrameType.CanDataFrame);
        Assert.False(frame.HasBitrateSwitch);
        Assert.False(frame.HasErrorStateIndicator);
    }

    [Fact]
    public void ErrorFrameHasNoIdButCarriesErrorBits()
    {
        var frame = new CanFrame();
        frame.SetFrameType(FrameType.ErrorFrame);
        frame.Error = FrameError.BusOff | FrameError.Transceiver;
        Assert.Equal(0U, frame.FrameId);
        Assert.Equal(FrameError.BusOff | FrameError.Transceiver, frame.Error);
    }

    [Fact]
    public void NonErrorFrameIgnoresErrorBits()
    {
        var frame = new CanFrame();
        frame.Error = FrameError.BusError;
        Assert.Equal(FrameError.None, frame.Error);
    }

    [Fact]
    public void FlexRayFramesUseSlotIdAndCycleByte()
    {
        var frame = new CanFrame();
        frame.SetFrameType(FrameType.FlexRayDataFrame);
        frame.SetFrameId(0x123);
        frame.SetPayload(new byte[255]);
        Assert.True(frame.IsFlexRay);
        Assert.True(frame.IsValid());

        frame.SetPayload(new byte[256]);
        Assert.False(frame.IsValid());
    }

    [Fact]
    public void FlexRayNullFrameKeepsHeaderButIsNotData()
    {
        var frame = new CanFrame();
        frame.SetFrameType(FrameType.FlexRayDataFrame);
        frame.SetFrameId(1);
        frame.SetPayload(new byte[] { 0 });
        frame.SetFlexRayHeader(0x1F, 0x123, 0x456);
        Assert.False(frame.IsFlexRayNullFrame);
        Assert.True(frame.IsDataFrame);

        frame.SetFlexRayNullFrame(true);
        Assert.True(frame.IsFlexRayNullFrame);
        Assert.False(frame.IsDataFrame);
    }

    [Fact]
    public void ExtendedFlagPreservedWhenSettingStandardId()
    {
        var frame = new CanFrame();
        frame.SetExtended(true);
        frame.SetFrameId(0x100);
        Assert.True(frame.IsExtended);
    }

    [Fact]
    public void CloneCopiesPayloadIndependently()
    {
        var frame = new CanFrame(0x123, [1, 2, 3]);
        var clone = frame.Clone();
        clone.Payload[0] = 9;
        Assert.Equal(1, frame.Payload[0]);
        Assert.Equal(9, clone.Payload[0]);
    }

    [Fact]
    public void ConstructorInfersFrameTypeFromPayload()
    {
        Assert.Equal(FrameType.CanDataFrame, new CanFrame(1, new byte[8]).FrameType);
        Assert.Equal(FrameType.CanFdDataFrame, new CanFrame(1, new byte[12]).FrameType);
    }
}
