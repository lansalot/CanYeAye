using System.Globalization;
using System.Text;

namespace CanYeAye.Core.FileFormats;

/// <summary>
/// CRTD / OVMS text log: "&lt;seconds&gt; [&lt;bus&gt;]R11|R29|T11|T29 &lt;id&gt; &lt;bytes...&gt;".
/// A timestamp without a decimal point is treated as already-microseconds (reference behaviour).
/// </summary>
public sealed class CrtdFormat : LogFormatBase
{
    public const int Version = 1;

    public override string Name => "CRTD Logs";

    public override string DefaultExtension => ".crt";

    public override bool CanSave => true;

    public override bool IsMatch(string path)
    {
        var lines = FormatHelpers.ReadLines(path);
        if (lines.Length < 2)
        {
            return false;
        }

        var matched = false;
        for (var i = 1; i < lines.Length && i <= 11; i++)
        {
            var line = FormatHelpers.Simplified(lines[i]);
            if (line.Length <= 2)
            {
                continue;
            }

            var tokens = line.Split(' ');
            if (tokens.Length < 3)
            {
                return false;
            }

            var type = tokens[1];
            if (type.Length > 0 && type[0] is >= '1' and <= '9')
            {
                type = type[1..];
            }

            if (type is "R29" or "T29" or "R11" or "T11")
            {
                matched = true;
            }
        }

        return matched;
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        var lines = FormatHelpers.ReadLines(path);
        var foundErrors = false;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = FormatHelpers.Simplified(lines[i]);
            if (line.Length <= 2)
            {
                continue;
            }

            var tokens = line.Split(' ');
            if (tokens.Length <= 2)
            {
                foundErrors = true;
                continue;
            }

            var multiplier = tokens[0].Contains('.') ? 1_000_000 : 1;
            var frame = new CanFrame
            {
                Bus = 0,
                Timestamp = FrameTimestamp.FromMicroseconds((long)(FormatHelpers.ParseDouble(tokens[0]) * multiplier)),
            };

            var firstChar = FormatHelpers.FirstChar(tokens[1]);
            if (firstChar is >= '1' and <= '9')
            {
                frame.Bus = (byte)(firstChar - '1');
                tokens[1] = tokens[1][1..];
                firstChar = FormatHelpers.FirstChar(tokens[1]);
            }

            if (firstChar is 'R' or 'T')
            {
                frame.SetFrameId((uint)FormatHelpers.ParseHex(tokens[2]));
                frame.SetExtended(tokens[1] is "R29" or "T29");
                frame.SetReceived(firstChar == 'R');
                frame.SetFrameType(FrameType.CanDataFrame);

                var payload = new byte[Math.Max(0, tokens.Length - 3)];
                for (var d = 0; d < payload.Length; d++)
                {
                    payload[d] = tokens[d + 3].Length == 0 ? (byte)0 : FormatHelpers.ParseByte(tokens[d + 3]);
                }

                frame.SetPayload(payload);
                frames.Add(frame);
            }
        }

        return !foundErrors;
    }

    public override bool Save(string path, IReadOnlyList<CanFrame> frames)
    {
        using var writer = new StreamWriter(path, false, Encoding.Latin1);
        var headerTime = frames.Count > 0 ? frames[0].Timestamp.ToMicroseconds() / 1_000_000.0 : 0;
        writer.Write(headerTime.ToString("F6", CultureInfo.InvariantCulture));
        writer.Write(" CXX GVRET-PC Reverse Engineering Tool Output V");
        writer.Write(Version.ToString(CultureInfo.InvariantCulture));
        writer.Write('\n');

        foreach (var frame in frames)
        {
            writer.Write((frame.Timestamp.ToMicroseconds() / 1_000_000.0).ToString("F6", CultureInfo.InvariantCulture));
            writer.Write(' ');
            writer.Write(frame.Bus + 1);
            writer.Write(frame.IsReceived ? 'R' : 'T');
            writer.Write(frame.IsExtended ? "29 " : "11 ");
            writer.Write(FormatHelpers.Hex8(frame.FrameId));
            writer.Write(' ');
            foreach (var b in frame.Payload)
            {
                writer.Write(b.ToString("X2", CultureInfo.InvariantCulture));
                writer.Write(' ');
            }

            writer.Write('\n');
        }

        return true;
    }
}
