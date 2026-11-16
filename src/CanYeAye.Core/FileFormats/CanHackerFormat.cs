namespace CanYeAye.Core.FileFormats;

/// <summary>
/// CANHacker trace: "HH[.,]MMM &lt;id&gt; &lt;len&gt; &lt;bytes...&gt;". Length is parsed as hex (reference
/// quirk) and a decimal comma is accepted as the timestamp separator.
/// </summary>
public sealed class CanHackerFormat : LogFormatBase
{
    public override string Name => "CANHacker Trace Files";

    public override string DefaultExtension => ".trc";

    public override bool IsMatch(string path)
    {
        var lines = FormatHelpers.ReadLines(path, maxLines: 11);
        if (lines.Length == 0)
        {
            return false;
        }

        if (lines[0].ToUpperInvariant().Contains("CANHACKER", StringComparison.Ordinal))
        {
            return true;
        }

        var isMatch = false;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = FormatHelpers.Simplified(lines[i]);
            if (line.Length <= 2)
            {
                continue;
            }

            var tokens = line.Split(' ');
            if (tokens.Length > 3)
            {
                if (FormatHelpers.ParseHex(tokens[1]) > 0)
                {
                    var length = FormatHelpers.ParseInt(tokens[2]);
                    if (length is > -1 and < 9)
                    {
                        isMatch = true;
                    }
                }
            }
            else
            {
                isMatch = false;
            }
        }

        return isMatch;
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        var lines = FormatHelpers.ReadLines(path);
        var foundErrors = false;
        var addendum = 0;
        var previous = 0.0;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = FormatHelpers.Simplified(lines[i]);
            if (line.Length <= 2)
            {
                continue;
            }

            var tokens = line.Split(' ');
            if (tokens.Length <= 3)
            {
                foundErrors = true;
                continue;
            }

            var multiplier = tokens[0].Contains('.') || tokens[0].Contains(',') ? 1_000_000 : 1;
            var value = FormatHelpers.ParseDouble(tokens[0].Replace(',', '.'));
            if (previous > value)
            {
                addendum += 60;
            }

            var frame = new CanFrame
            {
                Timestamp = FrameTimestamp.FromMicroseconds((long)((value + addendum) * multiplier)),
                Bus = 0,
            };

            previous = value;
            frame.SetFrameId((uint)FormatHelpers.ParseHex(tokens[1]));
            frame.SetExtended(frame.FrameId > 0x7FF);
            frame.SetReceived(true);
            frame.SetFrameType(FrameType.CanDataFrame);

            var length = (int)FormatHelpers.ParseHex(tokens[2]);
            if (length < 0 || length > tokens.Length - 3)
            {
                // A real trace always lists the bytes it claims. The reference instead indexes
                // past the token list, which lets its loader "succeed" on unrelated logs.
                return false;
            }

            var payload = new byte[length];
            for (var d = 0; d < length; d++)
            {
                if (tokens[d + 3].Length > 0)
                {
                    payload[d] = FormatHelpers.ParseByte(tokens[d + 3]);
                }
            }

            frame.SetPayload(payload);
            frames.Add(frame);
        }

        return !foundErrors;
    }
}
