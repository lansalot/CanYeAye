using System.Globalization;

namespace CanYeAye.Core.FileFormats;

/// <summary>
/// IXXAT CSV export (load only): a "Start Time" preamble and then a quoted table with
/// "Bus,No,Time,State,ID (hex),DLC,Data (hex),ASCII" columns.
/// </summary>
public sealed class IxxatCsvFormat : LogFormatBase
{
    public override string Name => "IXXAT CSV export";

    public override string DefaultExtension => ".csv";

    public override bool IsMatch(string path)
    {
        var lines = FormatHelpers.ReadLines(path, maxLines: 100);
        var headerFound = false;

        foreach (var rawLine in lines)
        {
            var tokens = FormatHelpers.SplitQuotedString(FormatHelpers.Simplified(rawLine));
            if (tokens.Count != 8)
            {
                continue;
            }

            if (!headerFound)
            {
                if (tokens[0] == "Bus" && tokens[1] == "No" && tokens[2] == "Time" &&
                    tokens[4] == "ID (hex)" && tokens[5] == "DLC" && tokens[6] == "Data (hex)")
                {
                    headerFound = true;
                }

                continue;
            }

            if (tokens[4] != "Error" &&
                !uint.TryParse(tokens[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }

            if (!uint.TryParse(tokens[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dlc))
            {
                return false;
            }

            if (tokens[6].Split(' ').Length != dlc)
            {
                return false;
            }
        }

        return headerFound;
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        var lines = FormatHelpers.ReadLines(path);
        if (lines.Length == 0)
        {
            return false;
        }

        ulong timeBase = 0;
        uint previousTime = 0;
        var headerLine = FormatHelpers.Simplified(lines[0]);
        if (headerLine.StartsWith("Start Time ", StringComparison.Ordinal))
        {
            var pieces = headerLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (pieces.Count >= 2)
            {
                pieces.RemoveRange(0, 2);
            }

            var text = string.Join(' ', pieces);
            if (DateTime.TryParseExact(text, "dddd MMMM dd yyyy HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var start))
            {
                timeBase = (ulong)new DateTimeOffset(start.ToUniversalTime()).ToUnixTimeMilliseconds();
            }
        }

        var headerFound = false;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = FormatHelpers.Simplified(lines[i]);
            var tokens = FormatHelpers.SplitQuotedString(line);
            if (tokens.Count != 8)
            {
                continue;
            }

            if (!headerFound)
            {
                if (tokens[0] == "Bus" && tokens[1] == "No" && tokens[2] == "Time" &&
                    tokens[4] == "ID (hex)" && tokens[5] == "DLC" && tokens[6] == "Data (hex)")
                {
                    headerFound = true;
                }

                continue;
            }

            var bus = 0;
            if (tokens[0].EndsWith('1'))
            {
                bus = 1;
            }
            else if (tokens[0].EndsWith('2'))
            {
                bus = 2;
            }

            var timeOffset = (uint)Math.Max(0, TimeOnly.TryParseExact(tokens[2], "HH:mm:ss.fff", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time)
                ? (int)time.ToTimeSpan().TotalMilliseconds
                : 0);

            if (tokens[4] == "Error")
            {
                continue;
            }

            var id = uint.Parse(tokens[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var dlc = uint.Parse(tokens[5], NumberStyles.Integer, CultureInfo.InvariantCulture);
            var data = tokens[6].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (data.Length != dlc)
            {
                continue;
            }

            if (previousTime > timeOffset)
            {
                timeBase += previousTime;
            }

            previousTime = timeOffset;

            var frame = new CanFrame
            {
                Bus = (byte)bus,
                Timestamp = FrameTimestamp.FromMicroseconds((long)((timeBase + timeOffset) * 1000)),
            };
            frame.SetFrameType(FrameType.CanDataFrame);
            frame.SetFrameId(id);
            frame.SetExtended(id > 0x7FF);
            frame.SetReceived(true);

            var payload = new byte[dlc];
            for (var d = 0; d < payload.Length; d++)
            {
                payload[d] = FormatHelpers.ParseByte(data[d]);
            }

            frame.SetPayload(payload);
            frames.Add(frame);
        }

        return headerFound;
    }
}
