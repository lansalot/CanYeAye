using System.Globalization;
using System.Text;

namespace CanYeAye.Core.FileFormats;

/// <summary>
/// Generic "ID,Data Bytes" CSV. No timestamps are stored, so loading synthesises 5 ms steps.
/// Detection deliberately tolerates the trailing space SavvyCAN's own writer emits (the
/// reference detector rejects its own output).
/// </summary>
public sealed class GenericCsvFormat : LogFormatBase
{
    public override string Name => "Generic ID/Data CSV";

    public override string DefaultExtension => ".csv";

    public override bool CanSave => true;

    public override bool IsMatch(string path)
    {
        var lines = FormatHelpers.ReadLines(path);
        if (lines.Length < 2)
        {
            return false;
        }

        var line = lines[1];
        if (line.Length <= 2)
        {
            return false;
        }

        var tokens = line.Split(',');
        if (tokens.Length < 2)
        {
            return false;
        }

        var id = FormatHelpers.ParseHex(tokens[0]);
        if (id < 1 || id > 0x1FFFFFFF)
        {
            return false;
        }

        var dataTokens = FormatHelpers.Simplified(tokens[1]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return dataTokens.Length is >= 2 and <= 8;
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        var lines = FormatHelpers.ReadLines(path);
        var time = FormatHelpers.MillisecondsSinceMidnight();
        var foundErrors = false;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length <= 2)
            {
                foundErrors = true;
                continue;
            }

            time += 5000;
            var tokens = line.Split(',');
            var frame = new CanFrame
            {
                Bus = 0,
                Timestamp = FrameTimestamp.FromMicroseconds(time),
            };
            frame.SetFrameId((uint)FormatHelpers.ParseHex(tokens[0]));
            frame.SetExtended(frame.FrameId > 0x7FF);
            frame.SetFrameType(FrameType.CanDataFrame);
            frame.SetReceived(true);

            if (tokens.Length > 1)
            {
                var dataTokens = FormatHelpers.Simplified(tokens[1]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (dataTokens.Length > 0)
                {
                    var payload = new byte[Math.Min(8, dataTokens.Length)];
                    for (var d = 0; d < payload.Length; d++)
                    {
                        payload[d] = FormatHelpers.ParseByte(dataTokens[d]);
                    }

                    frame.SetPayload(payload);
                }
            }

            frames.Add(frame);
        }

        return !foundErrors;
    }

    public override bool Save(string path, IReadOnlyList<CanFrame> frames)
    {
        using var writer = new StreamWriter(path, false, Encoding.Latin1);
        writer.Write("ID,Data Bytes\n");
        foreach (var frame in frames)
        {
            writer.Write(FormatHelpers.Hex8(frame.FrameId));
            writer.Write(',');
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
