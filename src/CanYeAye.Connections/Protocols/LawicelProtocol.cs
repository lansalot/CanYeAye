using System.Globalization;
using System.Text;
using CanYeAye.Core;

namespace CanYeAye.Connections.Protocols;

/// <summary>
/// Lawicel / CANUSB ASCII serial protocol (connections/lawicel_serial.cpp).
/// <para>
/// Lines are terminated by a carriage return and start with a command letter:
/// <c>t/T</c> classic frames (11/29-bit), <c>d/b/D/B</c> CAN FD frames (29-bit command
/// letters are uppercase, <c>b/B</c> additionally set the bit-rate switch). The same letters
/// are used when encoding, so a frame can be sent as soon as it has been decoded.
/// </para>
/// <para>
/// The reference expresses the transmit guard as <c>frameId() &amp; 0x20000000</c>, which can
/// never be true because <c>frameId()</c> masks the flag off. CanYeAye instead refuses to send
/// <see cref="FrameType.ErrorFrame"/> frames, which is what that guard intended.
/// </para>
/// </summary>
public sealed class LawicelProtocol
{
    private readonly StringBuilder _line = new();
    private long _timeBasis;
    private long _lastHardwareTimestamp = -1;
    private long _wrapAdder;

    /// <summary>Raised for every complete frame received from the adapter.</summary>
    public event EventHandler<CanFrame>? FrameReceived;

    /// <summary>
    /// When true, the adapter's wall clock is used instead of the optional 4-digit hardware
    /// millisecond suffix (SavvyCAN's <c>useSystemTime</c>).
    /// </summary>
    public bool UseSystemTime { get; set; }

    /// <summary>Drops received frames while capture is suspended (SavvyCAN's <c>isCapSuspended</c>).</summary>
    public bool CaptureSuspended { get; set; }

    /// <summary>
    /// Current wall clock in microseconds; replaceable so tests do not depend on the clock.
    /// </summary>
    public Func<long> NowMicroseconds { get; set; } =
        static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    /// <summary>Resets the wall-clock/hardware-timestamp pairing (called on connect).</summary>
    public void RebuildLocalTimeBasis()
    {
        _timeBasis = 0;
        _lastHardwareTimestamp = -1;
        _wrapAdder = 0;
    }

    /// <summary>Feeds raw serial bytes; complete CR-terminated lines are decoded.</summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            _line.Append((char)value);
            if (value != 0x0D)
            {
                continue;
            }

            var line = _line.ToString();
            _line.Clear();

            var frame = ParseLine(line);
            if (frame is not null && !CaptureSuspended)
            {
                FrameReceived?.Invoke(this, frame);
            }
        }
    }

    /// <summary>Decodes one CR-terminated line, or returns null when it is not a frame.</summary>
    public CanFrame? ParseLine(string line)
    {
        if (line.Length == 0)
        {
            return null;
        }

        var command = line[0];
        var isFd = command is 'b' or 'd' or 'B' or 'D';
        var isLongId = command is 'T' or 'B' or 'D';
        if (command is not ('t' or 'T' or 'b' or 'd' or 'B' or 'D'))
        {
            // SavvyCAN's parser computes offsets for unknown letters but its switch then emits
            // nothing, so unknown commands are silently dropped.
            return null;
        }

        var frame = new CanFrame
        {
            Timestamp = FrameTimestamp.FromMicroseconds(NowMicroseconds()),
        };

        ApplyHardwareTimestamp(line, isFd, isLongId, frame);

        switch (command)
        {
            case 't':
            {
                frame.SetFrameId((uint)ParseHex(line, 1, 3));
                frame.SetReceived(true);
                frame.SetFrameType(FrameType.CanDataFrame);
                var length = Math.Clamp(ParseDecimal(CharAt(line, 4)), 0, 8);
                frame.SetPayload(ReadData(line, 5, length));
                break;
            }

            case 'T':
            {
                frame.SetFrameId((uint)ParseHex(line, 1, 8));
                frame.SetExtended(true);
                frame.SetReceived(true);
                frame.SetFrameType(FrameType.CanDataFrame);
                var length = Math.Clamp(ParseDecimal(CharAt(line, 9)), 0, 8);
                frame.SetPayload(ReadData(line, 10, length));
                break;
            }

            case 'b':
            case 'd':
            {
                frame.SetBitrateSwitch(command == 'b');
                frame.SetFlexibleDataRate(true);
                frame.SetFrameId((uint)ParseHex(line, 1, 3));
                frame.SetReceived(true);
                frame.SetFrameType(FrameType.CanFdDataFrame);
                var length = DlcCodeToBytes(HexValue(CharAt(line, 4)));
                frame.SetPayload(ReadData(line, 5, length));
                break;
            }

            case 'B':
            case 'D':
            {
                frame.SetBitrateSwitch(command == 'B');
                frame.SetFlexibleDataRate(true);
                frame.SetFrameId((uint)ParseHex(line, 1, 8));
                frame.SetReceived(true);
                frame.SetFrameType(FrameType.CanFdDataFrame);
                frame.SetExtended(true);
                var length = DlcCodeToBytes(HexValue(CharAt(line, 9)));
                frame.SetPayload(ReadData(line, 10, length));
                break;
            }
        }

        return frame;
    }

    /// <summary>Encodes a frame as a CR-terminated Lawicel command.</summary>
    /// <returns>The command line, or null when the frame must not be sent to an adapter.</returns>
    public string? EncodeFrame(CanFrame frame)
    {
        if (frame.FrameType == FrameType.ErrorFrame)
        {
            return null;
        }

        var extended = frame.IsExtended;
        var id = frame.FrameId & (extended ? 0x1FFFFFFFU : 0x7FFU);
        var dlc = BytesToDlcCode(frame.Length);

        char command;
        if (frame.IsFlexibleDataRate)
        {
            command = (extended, frame.HasBitrateSwitch) switch
            {
                (true, true) => 'B',
                (true, false) => 'D',
                (false, true) => 'b',
                _ => 'd',
            };
        }
        else
        {
            command = extended ? 'T' : 't';
        }

        var builder = new StringBuilder(frame.Length * 2 + 16);
        builder.Append(command);
        builder.Append(extended
            ? id.ToString("X8", CultureInfo.InvariantCulture)
            : id.ToString("X3", CultureInfo.InvariantCulture));
        builder.Append(dlc.ToString("X", CultureInfo.InvariantCulture));
        foreach (var value in frame.Payload)
        {
            builder.Append(value.ToString("X2", CultureInfo.InvariantCulture));
        }

        builder.Append('\r');
        return builder.ToString();
    }

    /// <summary>
    /// The adapter's <c>S</c> code for a bus speed (SavvyCAN's <c>deviceConnected</c> mapping);
    /// unknown speeds fall back to 500 kbit/s (<c>6</c>).
    /// </summary>
    public static char EncodeBusSpeed(uint bitrate) => bitrate switch
    {
        10_000 => '0',
        20_000 => '1',
        50_000 => '2',
        100_000 => '3',
        125_000 => '4',
        250_000 => '5',
        500_000 => '6',
        800_000 => '7',
        1_000_000 => '8',
        _ => '6',
    };

    /// <summary>
    /// The adapter's <c>Y</c> data-phase rate (CAN FD); unknown rates fall back to <c>Y2</c>
    /// (2 Mbit/s), matching the reference's warning path.
    /// </summary>
    public static string EncodeDataRate(uint bitrate) => bitrate switch
    {
        1_000_000 => "Y1",
        2_000_000 => "Y2",
        4_000_000 => "Y4",
        5_000_000 => "Y5",
        _ => "Y2",
    };

    /// <summary>
    /// Builds the connect sequence: <c>C</c> (close), <c>S</c> (speed), optional <c>Y</c>
    /// (CAN FD data rate) and <c>O</c> (open).
    /// </summary>
    public static string BuildOpenSequence(uint bitrate, bool canFd, uint dataRate)
    {
        var builder = new StringBuilder();
        builder.Append("C\r");
        builder.Append('S').Append(EncodeBusSpeed(bitrate)).Append('\r');
        if (canFd)
        {
            builder.Append(EncodeDataRate(dataRate)).Append('\r');
        }

        builder.Append("O\r");
        return builder.ToString();
    }

    /// <summary>Payload length to the adapter's one-hex-digit DLC code.</summary>
    public static byte BytesToDlcCode(int length) => length switch
    {
        >= 0 and <= 8 => (byte)length,
        12 => 9,
        16 => 10,
        20 => 11,
        24 => 12,
        32 => 13,
        48 => 14,
        64 => 15,
        _ => 0,
    };

    /// <summary>The adapter's one-hex-digit DLC code expanded to a payload length.</summary>
    public static int DlcCodeToBytes(int code) => code switch
    {
        >= 0 and <= 8 => code,
        9 => 12,
        10 => 16,
        11 => 20,
        12 => 24,
        13 => 32,
        14 => 48,
        15 => 64,
        _ => 0,
    };

    private void ApplyHardwareTimestamp(string line, bool isFd, bool isLongId, CanFrame frame)
    {
        if (UseSystemTime)
        {
            return;
        }

        var offset = isLongId ? 10 : 5;
        var lengthIndex = isLongId ? 9 : 4;
        var dlc = HexValue(CharAt(line, lengthIndex));
        var byteCount = isFd ? DlcCodeToBytes(dlc) : dlc;
        var frameStringLength = offset + byteCount * 2;

        // The reference requires at least two characters past the data before it trusts the
        // suffix; the line still carries its terminating CR, so lengths line up exactly.
        if (line.Length <= frameStringLength + 1)
        {
            return;
        }

        var hardwareMs = ParseHex(line, frameStringLength, 4);
        if (_lastHardwareTimestamp >= 0 && hardwareMs < _lastHardwareTimestamp)
        {
            _wrapAdder += 60_000;
        }

        _lastHardwareTimestamp = hardwareMs;
        var unwrapped = _wrapAdder + hardwareMs;
        if (_timeBasis == 0)
        {
            _timeBasis = (NowMicroseconds() / 1000) - unwrapped;
        }

        frame.Timestamp = FrameTimestamp.FromMicroseconds((_timeBasis + unwrapped) * 1000);
    }

    private static byte[] ReadData(string line, int start, int length)
    {
        var payload = new byte[Math.Max(0, length)];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)ParseHex(line, start + (i * 2), 2);
        }

        return payload;
    }

    private static char CharAt(string line, int index) => index >= 0 && index < line.Length ? line[index] : '\0';

    private static int ParseDecimal(char value) => value is >= '0' and <= '9' ? value - '0' : 0;

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'F' => value - 'A' + 10,
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => -1,
    };

    /// <summary>Qt's <c>toInt(nullptr, 16)</c> semantics: an empty or invalid string yields 0.</summary>
    private static int ParseHex(string line, int start, int count)
    {
        if (count <= 0 || start < 0 || start + count > line.Length)
        {
            return 0;
        }

        var value = 0;
        for (var i = 0; i < count; i++)
        {
            var digit = HexValue(line[start + i]);
            if (digit < 0)
            {
                return 0;
            }

            value = (value * 16) + digit;
        }

        return value;
    }
}
