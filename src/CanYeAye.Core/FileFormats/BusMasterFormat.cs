using System.Globalization;
using System.Text;

namespace CanYeAye.Core.FileFormats;

/// <summary>
/// BusMaster log: a fixed "***" preamble followed by
/// "&lt;h:m:s:ms&gt; &lt;Rx|Tx&gt; &lt;channel&gt; 0x&lt;id&gt; &lt;s|x|sr|xr&gt; &lt;len&gt; &lt;bytes&gt;".
/// </summary>
public sealed class BusMasterFormat : LogFormatBase
{
    public override string Name => "BusMaster Log";

    public override string DefaultExtension => ".log";

    public override bool CanSave => true;

    public override bool IsMatch(string path)
    {
        var lines = FormatHelpers.ReadLines(path);
        if (lines.Length == 0)
        {
            return false;
        }

        if (!lines[0].ToUpperInvariant().Contains("BUSMASTER", StringComparison.Ordinal))
        {
            return false;
        }

        var index = 0;
        while (index < lines.Length && lines[index].StartsWith("***", StringComparison.Ordinal))
        {
            index++;
        }

        if (index >= lines.Length)
        {
            return false;
        }

        var line = lines[index].ToUpperInvariant();
        if (line.Length <= 1)
        {
            return false;
        }

        var tokens = line.Split(' ');
        if (tokens.Length < 6)
        {
            return false;
        }

        if (tokens[0].Split(':').Length != 4)
        {
            return false;
        }

        var id = ParseIdToken(tokens[3]);
        if (id < 1 || id > 0x1FFFFFFF)
        {
            return false;
        }

        if (tokens[4] is not ("S" or "X" or "SR" or "XR"))
        {
            return false;
        }

        return FormatHelpers.ParseInt(tokens[5]) <= 8;
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        var lines = FormatHelpers.ReadLines(path);
        var foundErrors = false;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].ToUpperInvariant();
            if (line.StartsWith("***", StringComparison.Ordinal) || line.Length <= 1)
            {
                continue;
            }

            var tokens = line.Split(' ');
            if (tokens.Length < 6)
            {
                foundErrors = true;
                continue;
            }

            var timeTokens = tokens[0].Split(':');
            if (timeTokens.Length < 4)
            {
                foundErrors = true;
                continue;
            }

            // The reference multiplies the millisecond field by 100 (a 100 µs unit bug). CanYeAye
            // uses the real millisecond value so round-trips through its own writer stay correct.
            var timestamp = (FormatHelpers.ParseInt(timeTokens[0]) * 3_600_000_000L) +
                            (FormatHelpers.ParseInt(timeTokens[1]) * 60_000_000L) +
                            (FormatHelpers.ParseInt(timeTokens[2]) * 1_000_000L) +
                            (FormatHelpers.ParseInt(timeTokens[3]) * 1000L);

            var frame = new CanFrame
            {
                Timestamp = FrameTimestamp.FromMicroseconds(timestamp),
                Bus = (byte)FormatHelpers.ParseInt(tokens[2]),
            };

            frame.SetReceived(FormatHelpers.FirstChar(tokens[1]) == 'R');
            frame.SetFrameId((uint)ParseIdToken(tokens[3]));

            switch (tokens[4])
            {
                case "S":
                    frame.SetExtended(false);
                    frame.SetFrameType(FrameType.CanDataFrame);
                    break;
                case "X":
                    frame.SetExtended(true);
                    frame.SetFrameType(FrameType.CanDataFrame);
                    break;
                case "SR":
                    frame.SetExtended(false);
                    frame.SetFrameType(FrameType.RemoteRequestFrame);
                    break;
                default:
                    frame.SetExtended(true);
                    frame.SetFrameType(FrameType.RemoteRequestFrame);
                    break;
            }

            var length = (int)Math.Clamp(FormatHelpers.ParseInt(tokens[5]), 0, 8);
            if (frame.FrameType != FrameType.RemoteRequestFrame)
            {
                // A data row must actually carry the bytes it claims; a remote row has none.
                length = Math.Min(length, Math.Max(0, tokens.Length - 6));
            }

            var payload = new byte[Math.Max(0, length)];
            if (frame.FrameType != FrameType.RemoteRequestFrame)
            {
                for (var d = 0; d < payload.Length; d++)
                {
                    payload[d] = FormatHelpers.ParseByte(tokens[d + 6]);
                }
            }

            frame.SetPayload(payload);
            frames.Add(frame);
        }

        return !foundErrors;
    }

    public override bool Save(string path, IReadOnlyList<CanFrame> frames)
    {
        using var writer = new StreamWriter(path, false, Encoding.Latin1);
        var now = DateTime.Now;
        writer.Write("***BUSMASTER Ver 3.2.0***\n");
        writer.Write("***PROTOCOL CAN***\n");
        writer.Write("***NOTE: PLEASE DO NOT EDIT THIS DOCUMENT***\n");
        writer.Write("***[START LOGGING SESSION]***\n");
        writer.Write("***START DATE AND TIME ");
        writer.Write(now.ToString("d:M:yyyy H:m:s:fff", CultureInfo.InvariantCulture));
        writer.Write("***\n");
        writer.Write("***HEX***\n");
        writer.Write("***SYSTEM MODE***\n");
        writer.Write("***START CHANNEL BAUD RATE***\n");
        writer.Write("***CHANNEL 1 - Kvaser - Kvaser Leaf Light HS #0 (Channel 0), Serial Number- 0, Firmware- 0x00000037 0x00020000 - 500000 bps***\n");
        writer.Write("***END CHANNEL BAUD RATE***\n");
        writer.Write("***START DATABASE FILES***\n");
        writer.Write("***END OF DATABASE FILES***\n");
        writer.Write("***<Time><Tx/Rx><Channel><CAN ID><Type><DLC><DataBytes>***\n");

        foreach (var frame in frames)
        {
            var stamp = DateTimeOffset.FromUnixTimeMilliseconds(frame.Timestamp.ToMicroseconds() / 1000).LocalDateTime;
            writer.Write(stamp.ToString("HH:mm:ss:fff", CultureInfo.InvariantCulture));
            writer.Write(frame.IsReceived ? " Rx " : " Tx ");
            writer.Write(frame.Bus.ToString(CultureInfo.InvariantCulture));
            writer.Write(" 0x");
            writer.Write(frame.IsExtended && frame.FrameId > 0x7FF
                ? FormatHelpers.Hex8(frame.FrameId)
                : frame.FrameId.ToString("X3", CultureInfo.InvariantCulture));
            writer.Write(frame.IsExtended ? " x" : " s");
            writer.Write(frame.FrameType == FrameType.RemoteRequestFrame ? "r " : " ");
            writer.Write(frame.Length.ToString(CultureInfo.InvariantCulture));
            writer.Write(' ');

            if (frame.FrameType != FrameType.RemoteRequestFrame)
            {
                foreach (var b in frame.Payload)
                {
                    writer.Write(b.ToString("X2", CultureInfo.InvariantCulture));
                    writer.Write(' ');
                }
            }

            writer.Write('\n');
        }

        return true;
    }

    /// <summary>
    /// Reads the ID column. Real BusMaster files print a bare zero-padded hex ID while SavvyCAN's
    /// own writer prefixes "0x"; the reference blindly drops two characters, which corrupts IDs
    /// shorter than eight characters. CanYeAye only strips an actual prefix.
    /// </summary>
    private static ulong ParseIdToken(string token) =>
        FormatHelpers.ParseHex(
            token.StartsWith("0X", StringComparison.Ordinal) ? token[2..] : token);
}


