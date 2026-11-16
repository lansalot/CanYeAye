using System.Globalization;
using System.Text;

namespace CanYeAye.Core.FileFormats;

/// <summary>
/// IXXAT MiniLog: seven header lines followed by
/// "\"h:m:s.frac\",\"id\",\"Std|Ext\",\"\",\"bb bb ...\"".
/// </summary>
public sealed class IxxatMiniLogFormat : LogFormatBase
{
    public override string Name => "IXXAT MiniLog";

    public override string DefaultExtension => ".csv";

    public override bool CanSave => true;

    public override bool IsMatch(string path)
    {
        var lines = FormatHelpers.ReadLines(path);
        if (lines.Length < 7)
        {
            return false;
        }

        if (!lines[0].ToUpperInvariant().Contains("IXXAT", StringComparison.Ordinal))
        {
            return false;
        }

        return lines[6].ToUpperInvariant().Contains("FORMAT", StringComparison.Ordinal);
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        var lines = FormatHelpers.ReadLines(path);
        for (var i = 7; i < lines.Length; i++)
        {
            var line = lines[i].ToUpperInvariant();
            if (line.Length <= 1)
            {
                continue;
            }

            var tokens = line.Split(',');
            if (tokens.Length < 5)
            {
                return false;
            }

            var timeParts = FormatHelpers.UnQuote(tokens[0]).Split(':');
            if (timeParts.Length < 3)
            {
                return false;
            }

            var timestamp = (FormatHelpers.ParseInt(timeParts[0]) * 3_600_000_000L) +
                            (FormatHelpers.ParseInt(timeParts[1]) * 60_000_000L) +
                            (long)(FormatHelpers.ParseDouble(timeParts[2]) * 1_000_000.0);

            var format = FormatHelpers.UnQuote(tokens[2]).ToUpperInvariant();
            if (format.Length == 0)
            {
                return false;
            }

            var frame = new CanFrame
            {
                Timestamp = FrameTimestamp.FromMicroseconds(timestamp),
                Bus = 0,
            };

            frame.SetFrameId((uint)FormatHelpers.ParseHex(FormatHelpers.UnQuote(tokens[1])));
            frame.SetExtended(format.Length == 0 || format[0] != 'S');
            frame.SetReceived(true);
            frame.SetFrameType(FrameType.CanDataFrame);

            var dataTokens = FormatHelpers.UnQuote(tokens[4])
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (dataTokens.Length > 8)
            {
                return false;
            }

            var payload = new byte[dataTokens.Length];
            for (var d = 0; d < payload.Length; d++)
            {
                payload[d] = FormatHelpers.ParseByte(dataTokens[d]);
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
        var last = frames.Count > 0 ? frames[^1].Timestamp.ToMicroseconds() : 0;
        var start = frames.Count > 0 ? frames[0].Timestamp.ToMicroseconds() : 0;

        writer.Write("ASCII Trace IXXAT SavvyCAN V1\n");
        writer.Write($"Date: {now:d:M:yyyy}\n");
        writer.Write($"Start time: {now.Hour}:{now.Minute}:{now.Second}\n");
        var stop = now.AddMilliseconds((last - start) / 1000.0);
        writer.Write($"Stop time: {stop.Hour}:{stop.Minute}:{stop.Second}\n");
        writer.Write("Overruns: 0\n");
        writer.Write("Baudrate: 500 kbit/s\n");
        writer.Write("\"Time\",\"Identifier (hex)\",\"Format\",\"Flags\",\"Data (hex)\"\n");

        foreach (var frame in frames)
        {
            var us = frame.Timestamp.ToMicroseconds();
            writer.Write('"');
            writer.Write((us / 3_600_000_000L % 24).ToString(CultureInfo.InvariantCulture));
            writer.Write(':');
            writer.Write((us / 60_000_000L % 60).ToString(CultureInfo.InvariantCulture));
            writer.Write(':');
            writer.Write((us / 1_000_000L % 60).ToString(CultureInfo.InvariantCulture));
            writer.Write('.');
            writer.Write((us % 1_000_000 / 1000).ToString("000", CultureInfo.InvariantCulture));
            writer.Write("\",\"");
            writer.Write(FormatHelpers.Hex8(frame.FrameId));
            writer.Write("\",\"");
            writer.Write(frame.IsExtended ? "Ext" : "Std");
            writer.Write("\",\"\",\"");
            foreach (var b in frame.Payload)
            {
                writer.Write(b.ToString("X2", CultureInfo.InvariantCulture));
                writer.Write(' ');
            }

            writer.Write("\"\n");
        }

        return true;
    }
}
