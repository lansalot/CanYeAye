using System.Globalization;
using System.Text;

namespace CanYeAye.Core.FileFormats;

/// <summary>
/// Cabana CSV: "time,addr,bus,data" where time is seconds, addr decimal and data a big-endian
/// hex word. Loading rebases timestamps to the first frame.
/// </summary>
public sealed class CabanaFormat : LogFormatBase
{
    public override string Name => "Cabana Log";

    public override string DefaultExtension => ".csv";

    public override bool CanSave => true;

    public override bool IsMatch(string path)
    {
        var lines = FormatHelpers.ReadLines(path);
        if (lines.Length == 0)
        {
            return false;
        }

        var header = lines[0].ToUpperInvariant();
        if (!header.Contains("TIME", StringComparison.Ordinal) || !header.Contains("ADDR", StringComparison.Ordinal))
        {
            return false;
        }

        var isMatch = true;
        for (var i = 1; i < lines.Length && i <= 100; i++)
        {
            var line = FormatHelpers.Simplified(lines[i]);
            if (line.Length <= 2)
            {
                continue;
            }

            var tokens = line.Split(',');
            if (tokens.Length is >= 3 and < 5)
            {
                var id = FormatHelpers.ParseInt(tokens[1]);
                if (id < 1 || id > 0x1FFFFFFF)
                {
                    isMatch = false;
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
        var baseSet = false;
        ulong timeBase = 0;
        ulong lastTimestamp = 0;
        var foundErrors = false;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = FormatHelpers.Simplified(lines[i]);
            if (line.Length <= 2)
            {
                continue;
            }

            var tokens = line.Split(',');
            if (tokens.Length < 3)
            {
                foundErrors = true;
                continue;
            }

            var timestamp = lastTimestamp + 1;
            if (tokens[0].Length > 1)
            {
                var micros = (ulong)(FormatHelpers.ParseDouble(tokens[0]) * 1_000_000.0);
                if (!baseSet)
                {
                    timeBase = micros;
                    baseSet = true;
                }

                timestamp = micros - timeBase;
                lastTimestamp = timestamp;
            }
            else
            {
                lastTimestamp = timestamp;
            }

            var frame = new CanFrame
            {
                Timestamp = FrameTimestamp.FromMicroseconds((long)timestamp),
                Bus = (byte)FormatHelpers.ParseInt(tokens[2]),
            };

            frame.SetFrameId((uint)FormatHelpers.ParseInt(tokens[1]));
            frame.SetExtended(frame.FrameId > 0x7FF);
            frame.SetFrameType(FrameType.CanDataFrame);
            frame.SetReceived(true);

            var dataToken = tokens.Length > 3 ? tokens[3] : string.Empty;
            var frameLength = Math.Min(8, dataToken.Length / 2);
            var word = FormatHelpers.ParseHex(dataToken);
            var bytes = new byte[frameLength];
            for (var b = 0; b < frameLength; b++)
            {
                bytes[b] = (byte)((word >> (8 * (frameLength - 1 - b))) & 0xFF);
            }

            frame.SetPayload(bytes);
            frames.Add(frame);
        }

        return !foundErrors;
    }

    public override bool Save(string path, IReadOnlyList<CanFrame> frames)
    {
        using var writer = new StreamWriter(path, false, Encoding.Latin1);
        writer.Write("time,addr,bus,data\n");
        foreach (var frame in frames)
        {
            writer.Write((frame.Timestamp.ToMicroseconds() / 1_000_000.0).ToString("F6", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(frame.FrameId.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(frame.Bus.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            for (var i = 0; i < frame.Length && i < 8; i++)
            {
                writer.Write(frame.Payload[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            writer.Write('\n');
        }

        return true;
    }
}
