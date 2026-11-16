using System.Globalization;
using System.Text;

namespace CanYeAye.Core.FileFormats;

/// <summary>
/// Tab-delimited "CAN Logger" trace files (SavvyCAN load filter 5 / save filter 5):
/// "&lt;msgNo&gt;\t&lt;HH:MM:SS:tttt&gt;\t&lt;id&gt;\t&lt;len&gt;\t&lt;bytes&gt;". Time field 4 is in 100 µs ticks.
/// </summary>
public sealed class VectorTraceFormat : LogFormatBase
{
    public override string Name => "Vector trace files";

    public override string DefaultExtension => ".trace";

    public override bool CanSave => true;

    public override bool IsMatch(string path)
    {
        var lines = FormatHelpers.ReadLines(path);
        var inComment = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith(';'))
            {
                inComment = true;
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            inComment = false;
            var tokens = line.Split('\t');
            if (tokens.Length <= 3)
            {
                return false;
            }

            if (tokens[1].Split(':').Length != 4)
            {
                return false;
            }

            var id = FormatHelpers.ParseHex(tokens[2]);
            if (id < 1 || id > 0x1FFFFFFF)
            {
                return false;
            }

            if (FormatHelpers.ParseInt(tokens[3]) > 8)
            {
                return false;
            }

            if (tokens.Length > 4)
            {
                var dataCount = FormatHelpers.Simplified(tokens[4]).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                if (FormatHelpers.ParseInt(tokens[4]) > dataCount)
                {
                    return false;
                }
            }
        }

        _ = inComment;
        return true;
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        foreach (var rawLine in FormatHelpers.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }

            var tokens = line.Split('\t');
            if (tokens.Length <= 3)
            {
                continue;
            }

            var timeParts = tokens[1].Split(':');
            if (timeParts.Length < 4)
            {
                continue;
            }

            var timestamp = (FormatHelpers.ParseInt(timeParts[0]) * 3_600_000_000L) +
                            (FormatHelpers.ParseInt(timeParts[1]) * 60_000_000L) +
                            (FormatHelpers.ParseInt(timeParts[2]) * 1_000_000L) +
                            (FormatHelpers.ParseInt(timeParts[3]) * 100L);

            var frame = new CanFrame
            {
                Timestamp = FrameTimestamp.FromMicroseconds(timestamp),
                Bus = 0,
            };

            frame.SetFrameId((uint)FormatHelpers.ParseHex(tokens[2]));
            frame.SetExtended(frame.FrameId > 0x7FF);
            frame.SetFrameType(FrameType.CanDataFrame);

            var length = (int)Math.Clamp(FormatHelpers.ParseInt(tokens[3]), 0, 8);
            var payload = new byte[length];
            if (tokens.Length > 4)
            {
                var dataTokens = FormatHelpers.Simplified(tokens[4]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (var d = 0; d < length && d < dataTokens.Length; d++)
                {
                    payload[d] = FormatHelpers.ParseByte(dataTokens[d]);
                }
            }

            frame.SetPayload(payload);
            frames.Add(frame);
        }

        return true;
    }

    public override bool Save(string path, IReadOnlyList<CanFrame> frames)
    {
        using var writer = new StreamWriter(path, false, Encoding.Latin1);
        var number = 0;
        foreach (var frame in frames)
        {
            var us = frame.Timestamp.ToMicroseconds();
            writer.Write(number.ToString(CultureInfo.InvariantCulture).PadLeft(10));
            writer.Write('\t');
            writer.Write((us / 3_600_000_000L % 24).ToString("00", CultureInfo.InvariantCulture));
            writer.Write(':');
            writer.Write((us / 60_000_000L % 60).ToString("00", CultureInfo.InvariantCulture));
            writer.Write(':');
            writer.Write((us / 1_000_000L % 60).ToString("00", CultureInfo.InvariantCulture));
            writer.Write(':');
            writer.Write((us % 1_000_000 / 100).ToString("0000", CultureInfo.InvariantCulture));
            writer.Write('\t');
            writer.Write(FormatHelpers.Hex8(frame.FrameId));
            writer.Write('\t');
            writer.Write(frame.Length.ToString(CultureInfo.InvariantCulture));
            writer.Write('\t');
            foreach (var b in frame.Payload)
            {
                writer.Write(b.ToString("X2", CultureInfo.InvariantCulture));
                writer.Write(' ');
            }

            writer.Write('\n');
            number++;
        }

        return true;
    }
}
