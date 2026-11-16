using System.Globalization;

namespace CanYeAye.Core.FileFormats;

/// <summary>
/// Lawicel/CANDump text export: each line is "&lt;8 hex chars&gt;" where the first three are the
/// 11-bit ID and the rest are data bytes. Timestamps are synthesised at 10 kHz.
/// </summary>
public sealed class LawicelFormat : LogFormatBase
{
    public override string Name => "CANDump Lawicel";

    public override string DefaultExtension => ".txt";

    public override bool IsMatch(string path)
    {
        var lines = FormatHelpers.ReadLines(path, maxLines: 100);
        var matched = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.ToUpperInvariant();
            if (line.StartsWith('S') || line.Length <= 4)
            {
                continue;
            }

            var id = FormatHelpers.ParseHex(line[..3]);
            if (id < 1 || id > 0x7FF)
            {
                continue;
            }

            var length = (line.Length - 3) / 2;
            if (length is >= 0 and <= 8)
            {
                matched = true;
            }
        }

        return matched;
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        long timestamp = 0;
        foreach (var rawLine in FormatHelpers.ReadLines(path))
        {
            var line = FormatHelpers.Simplified(rawLine);
            if (line.Length <= 4 || line.StartsWith('S'))
            {
                continue;
            }

            timestamp += 100;
            var frame = new CanFrame
            {
                Timestamp = FrameTimestamp.FromMicroseconds(timestamp),
                Bus = 0,
            };

            frame.SetFrameId((uint)FormatHelpers.ParseHex(line[..3]));
            frame.SetExtended(false);
            frame.SetReceived(true);
            frame.SetFrameType(FrameType.CanDataFrame);

            var rest = line[3..];
            var payload = new byte[rest.Length / 2];
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] = FormatHelpers.ParseByte(rest.Substring(i * 2, 2));
            }

            frame.SetPayload(payload);
            frames.Add(frame);
        }

        return true;
    }
}
