using System.Globalization;
using System.Text;

namespace CanYeAye.Core.FileFormats;

/// <summary>
/// Microchip CAN BUS Analyzer log: "&lt;ms&gt;;RX;0x&lt;id&gt;;&lt;len&gt;;0x&lt;byte&gt;;...". Lines between
/// "//" markers are comments; the markers toggle comment mode.
/// </summary>
public sealed class MicrochipFormat : LogFormatBase
{
    public override string Name => "Microchip Log";

    public override string DefaultExtension => ".log";

    public override bool CanSave => true;

    public override bool IsMatch(string path)
    {
        var lines = FormatHelpers.ReadLines(path, maxLines: 100);
        var inComment = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("//", StringComparison.Ordinal))
            {
                inComment = !inComment;
                continue;
            }

            if (inComment || line.Length == 0)
            {
                continue;
            }

            var tokens = TrimTrailingSeparator(line.Split(';'));
            if (tokens.Length < 4)
            {
                // No ';' at all means this is not a Microchip log; the reference behaves the
                // same way (it requires at least four fields on every non-comment line).
                return false;
            }

            var id = FormatHelpers.ParseStringToNum(tokens[2]);
            if (id < 1 || id > 0x1FFFFFFF)
            {
                return false;
            }

            var length = FormatHelpers.ParseStringToNum(tokens[3]);
            if (length > 8)
            {
                return false;
            }

            if (length + 4 < (ulong)tokens.Length)
            {
                return false;
            }
        }

        return true;
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        var lines = FormatHelpers.ReadLines(path);
        var inComment = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("//", StringComparison.Ordinal))
            {
                inComment = !inComment;
                continue;
            }

            if (inComment || line.Length == 0)
            {
                continue;
            }

            var tokens = TrimTrailingSeparator(line.Split(';'));
            if (tokens.Length < 4)
            {
                continue;
            }

            var frame = new CanFrame
            {
                Timestamp = FrameTimestamp.FromMicroseconds(FormatHelpers.ParseInt(tokens[0]) * 1000),
                Bus = 0,
            };

            frame.SetReceived(FormatHelpers.FirstChar(tokens[1]) == 'R');
            frame.SetFrameId((uint)FormatHelpers.ParseStringToNum(tokens[2]));
            frame.SetExtended(frame.FrameId > 0x7FF);
            frame.SetFrameType(FrameType.CanDataFrame);

            var length = (int)Math.Clamp(FormatHelpers.ParseStringToNum(tokens[3]), 0, 8);
            length = Math.Min(length, tokens.Length - 4);
            var payload = new byte[Math.Max(0, length)];
            for (var d = 0; d < payload.Length; d++)
            {
                payload[d] = (byte)FormatHelpers.ParseStringToNum(tokens[4 + d]);
            }

            frame.SetPayload(payload);
            frames.Add(frame);
        }

        return true;
    }

    public override bool Save(string path, IReadOnlyList<CanFrame> frames)
    {
        using var writer = new StreamWriter(path, false, Encoding.Latin1);
        var now = DateTime.Now;
        writer.Write("//---------------------------------\n");
        writer.Write("Microchip Technology Inc.\n");
        writer.Write("CAN BUS Analyzer\n");
        writer.Write("SavvyCAN Exporter\n");
        writer.Write("Logging Started: ");
        writer.Write(now.ToString("d/M/yyyy H:m:s", CultureInfo.InvariantCulture));
        writer.Write('\n');
        writer.Write("//---------------------------------\n");

        foreach (var frame in frames)
        {
            writer.Write((frame.Timestamp.ToMicroseconds() / 1000).ToString(CultureInfo.InvariantCulture));
            writer.Write(frame.IsReceived ? ";RX;" : ";TX;");
            writer.Write("0x");
            writer.Write(FormatHelpers.Hex8(frame.FrameId));
            writer.Write(';');
            writer.Write(frame.Length.ToString(CultureInfo.InvariantCulture));
            writer.Write(';');
            foreach (var b in frame.Payload)
            {
                writer.Write("0x");
                writer.Write(b.ToString("X2", CultureInfo.InvariantCulture));
                writer.Write(';');
            }

            writer.Write('\n');
        }

        return true;
    }

    private static string[] TrimTrailingSeparator(string[] tokens) =>
        tokens.Length > 0 && tokens[^1].Length == 0 ? tokens[..^1] : tokens;
}
