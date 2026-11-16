using System.Globalization;

namespace CanYeAye.Core.FileFormats;

/// <summary>
/// Kvaser ASCII log (load only): a fixed-column table with a CHN/FLG/D0 header and
/// <c>" 0  000000AD  8  FF FF ...  154.266550 R"</c> rows. The same layout is read twice with
/// either decimal or hexadecimal identifiers, matching SavvyCAN's two filter entries.
/// </summary>
public sealed class KvaserFormat(bool useHex) : LogFormatBase
{
    private readonly int _numberBase = useHex ? 16 : 10;

    public override string Name => useHex ? "Kvaser Log Hex" : "Kvaser Log Decimal";

    public override string DefaultExtension => ".txt";

    public override bool CanSave => false;

    public override bool IsMatch(string path)
    {
        var lines = FormatHelpers.ReadLines(path);
        if (lines.Length < 2)
        {
            return false;
        }

        var header = lines[0].ToUpperInvariant();
        if (!header.Contains("CHN", StringComparison.Ordinal) ||
            !header.Contains("FLG", StringComparison.Ordinal) ||
            !header.Contains("D0", StringComparison.Ordinal))
        {
            return false;
        }

        var checkedLines = 0;
        for (var i = 1; i < lines.Length && checkedLines < 10; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }

            checkedLines++;
            if (lines[i].Length <= 70)
            {
                return false;
            }

            var length = ParseColumn(lines[i], 21, 3);
            if (length is < 0 or > 8)
            {
                return false;
            }
        }

        return checkedLines > 0;
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        var lines = FormatHelpers.ReadLines(path);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].ToUpperInvariant();
            if (line.Length <= 72)
            {
                continue;
            }

            var frame = new CanFrame();
            frame.Bus = (byte)Math.Max(0, ParseColumn(line, 0, 3));
            frame.SetFrameId((uint)Math.Max(0, ParseColumn(line, 4, 10, _numberBase)));
            frame.SetExtended(frame.FrameId > 0x7FF);
            frame.SetFrameType(FrameType.CanDataFrame);
            frame.SetReceived(line[72] == 'R');
            frame.Timestamp = FrameTimestamp.FromMicroseconds(
                (long)Math.Round(ParseDouble(line, 57, 14) * 1_000_000d));

            var length = Math.Clamp(ParseColumn(line, 21, 3), 0, 8);
            var payload = new byte[length];
            for (var d = 0; d < length; d++)
            {
                payload[d] = (byte)Math.Max(0, ParseColumn(line, 25 + (d * 4), 3, _numberBase));
            }

            frame.SetPayload(payload);
            frames.Add(frame);
        }

        return true;
    }

    private static string Column(string line, int start, int length) =>
        start >= line.Length ? string.Empty : line.Substring(start, Math.Min(length, line.Length - start));

    private static int ParseColumn(string line, int start, int length, int numberBase = 10)
    {
        var text = Column(line, start, length).Trim();
        if (text.Length == 0)
        {
            return 0;
        }

        return int.TryParse(text, numberBase == 16 ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static double ParseDouble(string line, int start, int length)
    {
        var text = Column(line, start, length).Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
