namespace CanYeAye.Core.FileFormats;

/// <summary>
/// Vehicle Spy CSV export (load only): two header rows starting with "LINE", then rows where
/// field 1 is absolute seconds, 5 is direction, 9 is the arbitration ID and 12..19 the data.
/// </summary>
public sealed class VehicleSpyFormat : LogFormatBase
{
    public override string Name => "Vehicle Spy";

    public override string DefaultExtension => ".csv";

    public override bool IsMatch(string path)
    {
        var lines = FormatHelpers.ReadLines(path, maxLines: 11);
        if (lines.Length < 2)
        {
            return false;
        }

        var headerFound = false;
        for (var i = 0; i < Math.Min(10, lines.Length); i++)
        {
            var line = FormatHelpers.Simplified(lines[i]).ToUpperInvariant();
            if (line.Contains("LINE", StringComparison.Ordinal) &&
                line.Contains("TIME", StringComparison.Ordinal) &&
                line.Contains("B1", StringComparison.Ordinal))
            {
                headerFound = true;
                break;
            }
        }

        if (!headerFound || lines.Length <= 10)
        {
            return false;
        }

        var tokens = lines[10].Split(',');
        return tokens.Length > 20 && FormatHelpers.ParseHex(tokens[9]) > 0;
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        var lines = FormatHelpers.ReadLines(path);
        var foundHeader = false;
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var foundErrors = false;

        foreach (var line in lines)
        {
            var upper = line.ToUpperInvariant();
            if (upper.StartsWith("LINE", StringComparison.Ordinal))
            {
                if (foundHeader)
                {
                    break;
                }

                foundHeader = true;
                continue;
            }

            if (!foundHeader)
            {
                continue;
            }

            var tokens = line.Split(',');
            if (tokens.Length <= 20)
            {
                foundErrors = true;
                continue;
            }

            var seconds = FormatHelpers.ParseDouble(tokens[1]);
            var frame = new CanFrame
            {
                Bus = 0,
                Timestamp = FrameTimestamp.FromMicroseconds((now + (long)(seconds * 1000)) * 1000),
            };

            frame.SetReceived(FormatHelpers.FirstChar(tokens[5]) != 'T');
            frame.SetFrameId((uint)FormatHelpers.ParseHex(tokens[9]));
            frame.SetExtended(FormatHelpers.FirstChar(tokens[11]) == 'T');
            frame.SetFrameType(FrameType.CanDataFrame);

            List<byte> payload = [];
            for (var i = 12; i <= 19 && i < tokens.Length; i++)
            {
                if (tokens[i].Length == 0)
                {
                    break;
                }

                payload.Add(FormatHelpers.ParseByte(tokens[i]));
            }

            frame.SetPayload(payload.ToArray());
            frames.Add(frame);
        }

        if (!foundHeader)
        {
            foundErrors = true;
        }

        return !foundErrors;
    }
}
