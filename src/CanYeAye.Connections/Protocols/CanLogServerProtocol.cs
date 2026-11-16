using System.Globalization;
using System.Text;
using CanYeAye.Core;

namespace CanYeAye.Connections.Protocols;

/// <summary>
/// CANlogserver TCP ASCII stream (connections/canlogserver.cpp). The socket is line-oriented;
/// each trimmed line is <c>(&lt;seconds&gt;.&lt;fraction&gt;) &lt;bus&gt; &lt;id&gt;#&lt;payload&gt;</c>.
/// <para>
/// The timestamp is naively built by deleting <c>(</c>, <c>.</c> and <c>)</c> and reading the
/// rest as a microsecond count - <c>(166.064992)</c> becomes 166064992 µs. CAN FD frames use
/// <c>id##&lt;flags&gt;&lt;payload&gt;</c> with one hex flag digit (bit 0 = BRS, bit 1 = ESI).
/// The bus field is ignored: CANlogserver is a single-bus driver.
/// </para>
/// </summary>
public sealed class CanLogServerProtocol
{
    private readonly StringBuilder _pending = new();

    public bool CaptureSuspended { get; set; }

    public event EventHandler<CanFrame>? FrameReceived;

    /// <summary>Feeds decoded text from the socket; complete LF-terminated lines are parsed.</summary>
    public void Feed(string text)
    {
        if (CaptureSuspended)
        {
            return;
        }

        foreach (var c in text)
        {
            if (c == '\n')
            {
                var line = _pending.ToString().Trim();
                _pending.Clear();
                var frame = ParseLine(line);
                if (frame is not null)
                {
                    FrameReceived?.Invoke(this, frame);
                }
            }
            else
            {
                _pending.Append(c);
            }
        }
    }

    /// <summary>Parses one trimmed line, or returns null when it is not a CANlogserver frame.</summary>
    public static CanFrame? ParseLine(string line)
    {
        // QString::split(" ") keeps empty fields, so a double space rejects the line.
        var fields = line.Split(' ');
        if (fields.Length != 3)
        {
            return null;
        }

        var timestampText = fields[0].Replace("(", string.Empty)
            .Replace(".", string.Empty)
            .Replace(")", string.Empty);
        var timestamp = ulong.TryParse(timestampText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us)
            ? us
            : 0;

        var messageParts = fields[2].Split('#');
        var isFd = false;
        var flags = 0;
        if (messageParts.Length == 3 && messageParts[1].Length == 0 && messageParts[2].Length > 0)
        {
            isFd = true;
            flags = HexDigit(messageParts[2][0]);
            messageParts = [messageParts[0], messageParts[2][1..]];
        }

        if (messageParts.Length != 2)
        {
            return null;
        }

        var idText = messageParts[0];
        if (idText.Length != 8 && idText.Length > 4)
        {
            return null;
        }

        if (!TryParseHex(idText, out var id))
        {
            return null;
        }

        var frame = new CanFrame();
        frame.SetFrameId(id);
        frame.SetExtended(id > 0x7FF);
        frame.Bus = 0;
        frame.SetFrameType(isFd ? FrameType.CanFdDataFrame : FrameType.CanDataFrame);
        if (isFd)
        {
            frame.SetBitrateSwitch((flags & 1) != 0);
            frame.SetErrorStateIndicator((flags & 2) != 0);
        }

        frame.SetReceived(true);
        frame.Timestamp = FrameTimestamp.FromMicroseconds((long)timestamp);
        frame.SetPayload(DecodeHex(messageParts[1]));

        return frame;
    }

    private static byte[] DecodeHex(string text)
    {
        var payload = new byte[text.Length / 2];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((HexDigit(text[i * 2]) << 4) | HexDigit(text[(i * 2) + 1]));
        }

        return payload;
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => 0,
    };

    private static bool TryParseHex(string text, out uint value)
    {
        value = 0;
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var c in text)
        {
            var digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'A' and <= 'F' => c - 'A' + 10,
                >= 'a' and <= 'f' => c - 'a' + 10,
                _ => -1,
            };

            if (digit < 0)
            {
                return false;
            }

            value = (value * 16) + (uint)digit;
        }

        return true;
    }
}
