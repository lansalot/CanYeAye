using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CanYeAye.Core.FileFormats;

/// <summary>
/// CARBUS Analyzer trace (canhacker.ru text format). Files use bare CR line endings and start
/// with "@ TEXT @ &lt;version&gt; @ ...". Version 2 fractional timestamps are milliseconds,
/// version 3 are microseconds; wraps every minute are unwrapped.
/// </summary>
public sealed partial class CarBusFormat : LogFormatBase
{
    [GeneratedRegex(@"@ TEXT @ (?<version>\d) @.*")]
    private static partial Regex VersionRegex();

    public override string Name => "CARBUS Analyzer Trace Files";

    public override string DefaultExtension => ".trc";

    public override bool CanSave => true;

    public override bool IsMatch(string path)
    {
        var lines = FormatHelpers.ReadLines(path, rawNewlines: true, maxLines: 1);
        return lines.Length > 0 && lines[0].ToUpperInvariant().StartsWith("@ TEXT @", StringComparison.Ordinal);
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        var lines = FormatHelpers.ReadLines(path, rawNewlines: true);
        if (lines.Length == 0)
        {
            return false;
        }

        var version = 3;
        var match = VersionRegex().Match(lines[0].ToUpperInvariant());
        if (match.Success)
        {
            version = int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture);
        }

        long minuteCount = 0;
        long previous = 0;
        var foundErrors = false;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = FormatHelpers.Simplified(lines[i]);
            if (line.Length <= 2)
            {
                continue;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            var timeParts = tokens[0].Split(',');
            if (timeParts.Length != 2)
            {
                // "# $LocalTime=..." comment rows and other noise are skipped. The reference
                // flags them as errors, which makes it reject files that carry a LocalTime note.
                continue;
            }

            if (tokens.Length <= 3)
            {
                foundErrors = true;
                continue;
            }

            long timestamp;
            if (version == 2)
            {
                timestamp = (FormatHelpers.ParseInt(timeParts[0]) * 1_000_000L) +
                            (FormatHelpers.ParseInt(timeParts[1]) * 1000L);
            }
            else
            {
                timestamp = (FormatHelpers.ParseInt(timeParts[0]) * 1_000_000L) +
                            FormatHelpers.ParseInt(timeParts[1]);
            }

            timestamp += minuteCount * 60_000_000L;
            if (timestamp + 10_000 < previous)
            {
                minuteCount++;
                timestamp += 60_000_000L;
            }

            previous = timestamp;

            var frame = new CanFrame
            {
                Bus = (byte)FormatHelpers.ParseInt(tokens[1]),
                Timestamp = FrameTimestamp.FromMicroseconds(timestamp),
            };

            frame.SetReceived(true);
            frame.SetFrameType(FrameType.CanDataFrame);
            frame.SetFrameId((uint)FormatHelpers.ParseHex(tokens[3]));
            frame.SetExtended(frame.FrameId > 0x7FF);

            // The DLC column is decimal; the reference parses it as hex, which breaks any
            // CAN FD frame with a DLC above 9.
            var length = (int)FormatHelpers.ParseInt(tokens[4]);
            length = Math.Clamp(length, 0, 64);
            var payload = new byte[length];
            for (var d = 0; d < length; d++)
            {
                payload[d] = 5 + d < tokens.Length && tokens[5 + d].Length > 0
                    ? FormatHelpers.ParseByte(tokens[5 + d])
                    : (byte)0;
            }

            frame.SetPayload(payload);
            frames.Add(frame);
        }

        return !foundErrors;
    }

    public override bool Save(string path, IReadOnlyList<CanFrame> frames)
    {
        using var writer = new StreamWriter(path, false, Encoding.Latin1);
        if (frames.Count == 0)
        {
            writer.Write("@ TEXT @ 3 @ 64 @ 0 @ 0 @ 0 @ 00:00:00.000 @\r");
            return true;
        }

        var minTime = frames[0].Timestamp.ToMicroseconds();
        var maxTime = minTime;
        foreach (var f in frames)
        {
            minTime = Math.Min(minTime, f.Timestamp.ToMicroseconds());
            maxTime = Math.Max(maxTime, f.Timestamp.ToMicroseconds());
        }

        var totalMs = (maxTime - minTime) / 1000;
        var someTime = TimeSpan.FromMilliseconds(totalMs);
        writer.Write($"@ TEXT @ 3 @ 64 @ 0 @ {frames.Count} @ {totalMs} @ {someTime.Hours:00}:{someTime.Minutes:00}:{someTime.Seconds:00}.{someTime.Milliseconds:000} @\r");

        foreach (var frame in frames)
        {
            var timestamp = frame.Timestamp.ToMicroseconds() % 60_000_000L;
            var id = frame.FrameId.ToString("X", CultureInfo.InvariantCulture).PadLeft(3, '0');

            var data = new StringBuilder();
            var ascii = new StringBuilder();
            foreach (var b in frame.Payload)
            {
                data.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                data.Append(' ');
                ascii.Append(b is >= 32 and < 126 ? (char)b : ' ');
            }

            var dataCell = data.ToString().Trim().PadRight(23);
            var asciiCell = ascii.ToString().PadRight(8);

            writer.Write((timestamp / 1_000_000).ToString("00", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write((timestamp % 1_000_000).ToString("000000", CultureInfo.InvariantCulture));
            writer.Write('\t');
            writer.Write(frame.Bus.ToString(CultureInfo.InvariantCulture));
            writer.Write("\t0004\t");
            writer.Write(id);
            writer.Write('\t');
            writer.Write(frame.Length.ToString(CultureInfo.InvariantCulture));
            writer.Write('\t');
            writer.Write(dataCell);
            writer.Write("\t00000000\t");
            writer.Write(asciiCell);
            writer.Write("\t\r");
        }

        return true;
    }
}
