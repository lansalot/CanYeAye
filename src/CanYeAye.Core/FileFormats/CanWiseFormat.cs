using System.Globalization;
using System.Text.RegularExpressions;

namespace CanYeAye.Core.FileFormats;

/// <summary>
/// CANWise log: a header row followed by fixed-shape rows
/// "RX|TX &lt;num&gt; EFF|SFF &lt;id&gt; &lt;dlc&gt; HEX &lt;bytes&gt; &lt;timestamp µs&gt; ...".
/// </summary>
public sealed partial class CanWiseFormat : LogFormatBase
{
    [GeneratedRegex(@"^(?<dir>[RT]X) (?<number>\d+) (?<ext>[ES]FF) (?<id>[\dA-F]+) (?<dlc>\d) HEX +(?<data>(?:[\dA-F]{2} +)+)(?<timestamp>\d+)")]
    private static partial Regex DataRowRegex();

    public override string Name => "CANWise Log";

    public override string DefaultExtension => ".log";

    public override bool IsMatch(string path)
    {
        var lines = FormatHelpers.ReadLines(path);
        if (lines.Length < 2)
        {
            return false;
        }

        return DataRowRegex().IsMatch(lines[1]);
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        var lines = FormatHelpers.ReadLines(path);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var match = DataRowRegex().Match(line);
            if (!match.Success)
            {
                // The reference skips only the first (header) line, then gives up.
                if (i == 0)
                {
                    continue;
                }

                return false;
            }

            var dataTokens = match.Groups["data"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var length = int.Parse(match.Groups["dlc"].Value, CultureInfo.InvariantCulture);
            if (length != dataTokens.Length)
            {
                return false;
            }

            var frame = new CanFrame
            {
                Bus = 0,
                Timestamp = FrameTimestamp.FromMicroseconds((long)FormatHelpers.ParseStringToNum(match.Groups["timestamp"].Value)),
            };

            frame.SetReceived(match.Groups["dir"].Value != "TX");
            frame.SetExtended(match.Groups["ext"].Value == "EFF");
            frame.SetFrameId((uint)FormatHelpers.ParseHex(match.Groups["id"].Value));
            frame.SetFrameType(FrameType.CanDataFrame);

            var payload = new byte[length];
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
