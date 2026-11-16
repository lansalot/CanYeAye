namespace CanYeAye.Core.FileFormats;

/// <summary>
/// CANOpen Magic CSV export (load only). Five header lines, then comma-separated rows where
/// field 1 is milliseconds, field 5 the ID and field 11 the hex data bytes.
/// </summary>
public sealed class CanOpenFormat : LogFormatBase
{
    public override string Name => "CANOpen Magic";

    public override string DefaultExtension => ".csv";

    public override bool IsMatch(string path)
    {
        var lines = FormatHelpers.ReadLines(path);
        if (lines.Length == 0 ||
            !lines[0].ToUpperInvariant().Contains("CANOPEN MAGIC", StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = 5; i < lines.Length && i <= 15; i++)
        {
            var line = FormatHelpers.Simplified(lines[i].Replace('"', ' '));
            var tokens = line.Split(',');
            if (tokens.Length <= 11)
            {
                continue;
            }

            if (FormatHelpers.ParseStringToNum(tokens[5]) > 0 &&
                FormatHelpers.Simplified(tokens[11]).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 9)
            {
                return true;
            }
        }

        return false;
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        var lines = FormatHelpers.ReadLines(path);
        for (var i = 5; i < lines.Length; i++)
        {
            var line = FormatHelpers.Simplified(lines[i].Replace('"', ' '));
            var tokens = line.Split(',');
            if (tokens.Length <= 11)
            {
                continue;
            }

            var frame = new CanFrame
            {
                Timestamp = FrameTimestamp.FromMicroseconds((long)(FormatHelpers.ParseDouble(tokens[1]) * 1000.0)),
                Bus = 0,
            };

            frame.SetFrameId((uint)FormatHelpers.ParseStringToNum(tokens[5]));
            frame.SetExtended(frame.FrameId > 0x7FF);
            frame.SetReceived(true);
            frame.SetFrameType(FrameType.CanDataFrame);

            var dataTokens = FormatHelpers.Simplified(tokens[11]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var payload = new byte[Math.Min(64, dataTokens.Length)];
            for (var d = 0; d < payload.Length; d++)
            {
                payload[d] = FormatHelpers.ParseByte(dataTokens[d]);
            }

            frame.SetPayload(payload);
            frames.Add(frame);
        }

        return true;
    }
}
