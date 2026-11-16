using CanYeAye.Core;
using CanYeAye.Core.Dbc;

namespace CanYeAye.Tests.Dbc;

public class SignalCodecTests
{
    private static CanFrame Frame(params byte[] data)
    {
        var frame = new CanFrame();
        frame.SetPayload(data);
        frame.SetFrameType(FrameType.CanDataFrame);
        return frame;
    }

    [Theory]
    // Intel 0|16 over {0x10, 0x27} -> 0x2710 (little endian).
    [InlineData(true, false, 0, 16, new byte[] { 0x10, 0x27 }, 10000UL)]
    // Motorola 39|8 with byte 4 = 0xFF -> 255.
    [InlineData(false, false, 39, 8, new byte[] { 0, 0, 0, 0, 0xFF }, 255UL)]
    // Intel 0|12 signed over {0xFE, 0x0F} -> -2.
    [InlineData(true, true, 0, 12, new byte[] { 0xFE, 0x0F }, unchecked((ulong)-2))]
    // Motorola 7|16 signed over {0xFF, 0xFE} -> -2 (big-endian word 0xFFFE).
    [InlineData(false, true, 7, 16, new byte[] { 0xFF, 0xFE }, unchecked((ulong)-2))]
    public void ExtractsBits(bool intel, bool signed, int startBit, int size, byte[] data, ulong expected)
    {
        Assert.Equal(expected, SignalCodec.ExtractBits(data, startBit, size, intel, signed));
    }

    [Fact]
    public void MotorolaCrossesByteBoundary()
    {
        // Motorola 23|12 crosses from byte 2 (bit 23) down into byte 1 (bit 12).
        var data = new byte[4];
        SignalCodec.InsertBits(data, 23, 12, false, 0x0ABC);
        Assert.Equal(0x0ABCu, SignalCodec.ExtractBits(data, 23, 12, false, false));
    }

    [Fact]
    public void MotorolaLowStartBitReadsBigEndianWord()
    {
        // Start bit 7 (LSB of byte 0) motorola, 16 bits: big-endian word over bytes 0-1.
        var data = new byte[] { 0x12, 0x34, 0, 0 };
        Assert.Equal(0x1234u, SignalCodec.ExtractBits(data, 7, 16, false, false));
    }

    [Fact]
    public void InsertAndExtractRoundTripBothOrders()
    {
        var data = new byte[8];

        SignalCodec.InsertBits(data, 12, 8, true, 0xA5);
        Assert.Equal(0xA5u, SignalCodec.ExtractBits(data, 12, 8, true, false));

        SignalCodec.InsertBits(data, 7, 16, false, 0x1234);
        Assert.Equal(0x1234u, SignalCodec.ExtractBits(data, 7, 16, false, false));
    }

    [Fact]
    public void OutOfRangeSignalYieldsZero()
    {
        Assert.Equal(0u, SignalCodec.ExtractBits(new byte[1], 8, 16, true, false));
    }

    [Fact]
    public void LastByteOfSignalHandlesMotorolaMsb()
    {
        Assert.Equal(0, SignalCodec.LastByteOfSignal(0, 8, true));
        Assert.Equal(4, SignalCodec.LastByteOfSignal(39, 8, false));
    }

    [Fact]
    public void DecodesSingleAndDoubleFloats()
    {
        var singleBits = BitConverter.SingleToInt32Bits(42.5f);
        var singleData = BitConverter.GetBytes(singleBits);
        var singlePayload = new byte[4];
        singleData.CopyTo(singlePayload, 0);

        Assert.True(SignalCodec.TryProcessFloat(singlePayload, 0, 32, true, DbcSignalValueType.SingleFloat, out var s));
        Assert.Equal(42.5, s, 5);

        var doubleBits = BitConverter.DoubleToInt64Bits(123.25);
        var doubleData = BitConverter.GetBytes(doubleBits);
        Assert.True(SignalCodec.TryProcessFloat(doubleData, 0, 64, true, DbcSignalValueType.DoubleFloat, out var d));
        Assert.Equal(123.25, d, 6);
    }

    [Fact]
    public void StringDecodeKeepsEmbeddedNuls()
    {
        var data = new byte[] { (byte)'A', (byte)'B', 0, (byte)'C' };
        Assert.Equal("AB\u0000C", SignalCodec.DecodeString(data, 0, 32));
    }

    [Fact]
    public void IntegerSignalsScaleWithFactorAndBias()
    {
        // Build a message/signal via a minimal inline DBC-like object graph.
        var signal = new DbcSignal
        {
            StartBit = 0,
            SignalSize = 8,
            IntelByteOrder = true,
            ValueType = DbcSignalValueType.UnsignedInt,
            Factor = 2.0,
            Bias = 1.0,
            Receiver = new DbcNode { Name = "V" },
            ParentMessage = new DbcMessage(),
        };

        var frame = Frame(10);
        Assert.True(signal.TryProcessAsInt(frame, out var value));
        Assert.Equal(21, value); // 10 * 2 + 1

        // Non-integer factor switches to float text formatting.
        signal.Factor = 0.5;
        signal.Bias = 0;
        var text = signal.ProcessAsText(Frame(10), outputName: false, outputUnit: false).Trim();
        Assert.Equal("5", text);
    }

    [Fact]
    public void ValueDescriptionsOverrideNumericText()
    {
        var signal = new DbcSignal
        {
            StartBit = 0,
            SignalSize = 8,
            IntelByteOrder = true,
            ValueType = DbcSignalValueType.UnsignedInt,
            Factor = 1.0,
            Receiver = new DbcNode { Name = "V" },
            ParentMessage = new DbcMessage(),
        };
        signal.ValueDescriptions.Add(new DbcValueEntry(1, "Error present"));
        signal.ValueDescriptions.Add(new DbcValueEntry(0, "Error not present"));

        Assert.Equal("Error present", signal.ProcessAsText(Frame(1), outputName: false, outputUnit: false).Trim());
    }
}
