namespace WinCAN.Core.Dbc;

/// <summary>
/// The sawtooth bit extraction / insertion core (Utility::processIntegerSignal and friends).
/// Intel advances +1 per bit; Motorola wraps to the next byte from its last LSB, so start bit 12
/// size 8 reads bits 12,11,10,9,8,23,22,21.
/// </summary>
public static class SignalCodec
{
    /// <summary>
    /// Extracts <paramref name="size"/> bits starting at <paramref name="startBit"/>. Signed values
    /// are sign-extended to 64 bits; out-of-range signals yield 0 (not an error).
    /// </summary>
    public static ulong ExtractBits(
        ReadOnlySpan<byte> data,
        int startBit,
        int size,
        bool littleEndian,
        bool isSigned)
    {
        if (size <= 0 || startBit < 0 || (startBit + size) / 8 > data.Length)
        {
            return 0;
        }

        ulong result = 0;
        var bit = startBit;
        for (var bitPos = 0; bitPos < size; bitPos++)
        {
            if (bit < 2048)
            {
                var bytePos = bit / 8;
                if (bytePos >= data.Length)
                {
                    return 0;
                }

                if ((data[bytePos] & (1 << (bit % 8))) != 0)
                {
                    result |= 1UL << (littleEndian ? bitPos : size - bitPos - 1);
                }
            }

            bit += littleEndian ? 1 : ((bit % 8) == 0 ? 15 : -1);
        }

        if (isSigned)
        {
            result = SignExtend(result, size);
        }

        return result;
    }

    /// <summary>Writes <paramref name="raw"/> into the target bits, clearing them first.</summary>
    public static void InsertBits(
        Span<byte> data,
        int startBit,
        int size,
        bool littleEndian,
        ulong raw)
    {
        if (size <= 0 || startBit < 0)
        {
            return;
        }

        // Clear the target bits so overlapping definitions behave deterministically.
        var clearBit = startBit;
        for (var bitPos = 0; bitPos < size; bitPos++)
        {
            var bytePos = clearBit / 8;
            if (clearBit >= 0 && bytePos < data.Length)
            {
                data[bytePos] &= (byte)~(1 << (clearBit % 8));
            }

            clearBit += littleEndian ? 1 : ((clearBit % 8) == 0 ? 15 : -1);
        }

        var bit = startBit;
        for (var bitPos = 0; bitPos < size; bitPos++)
        {
            var bytePos = bit / 8;
            if (bit >= 0 && bytePos < data.Length)
            {
                var valueBit = littleEndian ? bitPos : size - bitPos - 1;
                if ((raw & (1UL << valueBit)) != 0)
                {
                    data[bytePos] |= (byte)(1 << (bit % 8));
                }
            }

            bit += littleEndian ? 1 : ((bit % 8) == 0 ? 15 : -1);
        }
    }

    /// <summary>Last payload byte the signal touches (bounds check for the double decode path).</summary>
    public static int LastByteOfSignal(int startBit, int size, bool littleEndian)
    {
        if (littleEndian)
        {
            return (startBit + size - 1) / 8;
        }

        var msb = (startBit / 8) * 8 + (7 - (startBit % 8));
        return (msb + size - 1) / 8;
    }

    /// <summary>Reinterprets extracted bits as a single- or double-precision float.</summary>
    public static bool TryProcessFloat(
        ReadOnlySpan<byte> data,
        int startBit,
        int size,
        bool littleEndian,
        DbcSignalValueType valueType,
        out double value)
    {
        value = 0;
        if (valueType == DbcSignalValueType.SingleFloat)
        {
            if ((long)startBit + 32 > (long)data.Length * 8)
            {
                return false;
            }

            var bits = (uint)ExtractBits(data, startBit, 32, littleEndian, isSigned: false);
            value = BitConverter.Int32BitsToSingle((int)bits);
            return true;
        }

        if (valueType == DbcSignalValueType.DoubleFloat)
        {
            if ((long)startBit + 64 > (long)data.Length * 8)
            {
                return false;
            }

            var bits = ExtractBits(data, startBit, 64, littleEndian, isSigned: false);
            value = BitConverter.Int64BitsToDouble(unchecked((long)bits));
            return true;
        }

        return false;
    }

    /// <summary>Decodes a string signal: consecutive bytes starting at startBit/8, Latin-1.</summary>
    public static string DecodeString(ReadOnlySpan<byte> data, int startBit, int size)
    {
        var startByte = startBit / 8;
        var count = size / 8;
        if (startByte < 0 || count <= 0 || startByte >= data.Length)
        {
            return string.Empty;
        }

        count = Math.Min(count, data.Length - startByte);
        var chars = new char[count];
        for (var i = 0; i < count; i++)
        {
            chars[i] = (char)data[startByte + i];
        }

        return new string(chars);
    }

    private static ulong SignExtend(ulong value, int size)
    {
        if (size >= 64)
        {
            return value;
        }

        var mask = 1UL << (size - 1);
        if ((value & mask) != 0)
        {
            value |= ~((1UL << size) - 1);
        }

        return value;
    }
}
