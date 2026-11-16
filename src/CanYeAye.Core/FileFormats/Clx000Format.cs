using System.Globalization;
using System.Text.RegularExpressions;

namespace CanYeAye.Core.FileFormats;

/// <summary>
/// CLX000 text log (load only). The header declares the value/time/date separators and a time
/// format level; the loader builds a regex from those (as the reference does) and reconstructs
/// absolute timestamps from the declared start time.
/// </summary>
public sealed partial class Clx000Format : LogFormatBase
{
    private const string ValidSeparatorsDetect = " -~";
    private const string ValidSeparatorsLoad = " -~;";

    public override string Name => "CLX000";

    public override string DefaultExtension => ".txt";

    public override bool CanSave => false;

    public override bool IsMatch(string path)
    {
        return TryReadHeader(path, ValidSeparatorsDetect, out _);
    }

    public override bool Load(string path, List<CanFrame> frames)
    {
        if (!TryReadHeader(path, ValidSeparatorsLoad, out var header))
        {
            return false;
        }

        var lines = FormatHelpers.ReadLines(path);
        var pattern = BuildPattern(header);
        var regex = new Regex(pattern);
        var start = header.StartTime;

        for (var i = 17; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            var match = regex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var year = GroupOr(match, "year", start.Year);
            var month = GroupOr(match, "month", start.Month);
            var day = GroupOr(match, "day", start.Day);
            var hours = GroupOr(match, "hours", start.Hour);
            var minutes = GroupOr(match, "minutes", start.Minute);
            var seconds = GroupOr(match, "seconds", start.Second);
            var milliseconds = GroupOr(match, "ms", start.Millisecond);

            DateTime timestamp;
            try
            {
                timestamp = new DateTime(year, month, day, hours, minutes, seconds, milliseconds, DateTimeKind.Local);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            var frame = new CanFrame();
            frame.SetFrameType(FrameType.CanDataFrame);
            frame.Bus = 0;
            frame.Timestamp = FrameTimestamp.FromMicroseconds(
                new DateTimeOffset(timestamp).ToUnixTimeMilliseconds() * 1000);

            var hasType = match.Groups["type"].Success && match.Groups["type"].Value.Length > 0;
            if (hasType)
            {
                var type = ParseInt(match.Groups["type"].Value);
                frame.SetReceived((type & 0x08) == 0);
                frame.SetExtended((type & 0x01) != 0);
            }

            if (match.Groups["id"].Success && match.Groups["id"].Value.Length > 0)
            {
                var id = uint.Parse(match.Groups["id"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                frame.SetFrameId(id);
                if (!hasType)
                {
                    frame.SetExtended(id > 0x7FF);
                }
            }

            if (match.Groups["data"].Success && match.Groups["data"].Value.Length > 0)
            {
                var hex = match.Groups["data"].Value;
                if (hex.Length % 2 != 0)
                {
                    continue;
                }

                frame.SetPayload(Convert.FromHexString(hex));
            }
            else
            {
                frame.SetPayload(ReadOnlySpan<byte>.Empty);
            }

            frames.Add(frame);
        }

        return true;
    }

    private static int GroupOr(Match match, string name, int fallback)
    {
        var group = match.Groups[name];
        return group.Success && group.Value.Length > 0
            ? ParseInt(group.Value)
            : fallback;
    }

    private static int ParseInt(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private sealed record ClxHeader(
        DateTime StartTime,
        char ValueSeparator,
        int TimeFormat,
        char TimeSeparator,
        char TimeSeparatorMs,
        char DateSeparator,
        char TimeDateSeparator,
        IReadOnlyList<string> Fields);

    private static bool TryReadHeader(string path, string validSeparators, out ClxHeader header)
    {
        header = null!;
        string[] lines;
        try
        {
            lines = FormatHelpers.ReadLines(path);
        }
        catch (IOException)
        {
            return false;
        }

        if (lines.Length < 17 || !lines[0].StartsWith("# Logger type: ", StringComparison.Ordinal))
        {
            return false;
        }

        if (!lines[6].StartsWith("# Time: ", StringComparison.Ordinal))
        {
            return false;
        }

        var timeText = lines[6].Length >= 15 ? lines[6][^15..] : string.Empty;
        if (!DateTime.TryParseExact(timeText, "yyyyMMddThhmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var startTime))
        {
            return false;
        }

        if (!TryParseQuotedChar(lines[7], "Value separator", validSeparators, required: true, out var valueSeparator))
        {
            return false;
        }

        if (!TryParseTimeFormat(lines[8], out var timeFormat))
        {
            return false;
        }

        if (!TryParseQuotedChar(lines[9], "Time separator", validSeparators, required: false, out var timeSeparator) ||
            !TryParseQuotedChar(lines[10], "Time separator ms", validSeparators, required: false, out var timeSeparatorMs) ||
            !TryParseQuotedChar(lines[11], "Date separator", validSeparators, required: false, out var dateSeparator) ||
            !TryParseQuotedChar(lines[12], "Time and date separator", validSeparators, required: false, out var timeDateSeparator))
        {
            return false;
        }

        var fields = lines[16].Split(valueSeparator == '\0' ? ' ' : valueSeparator);
        string[] valid = ["Timestamp", "Type", "ID", "Length", "Data"];
        if (!fields.All(f => valid.Contains(f, StringComparer.Ordinal)))
        {
            return false;
        }

        header = new ClxHeader(
            startTime,
            valueSeparator,
            timeFormat,
            timeSeparator,
            timeSeparatorMs,
            dateSeparator,
            timeDateSeparator,
            fields);
        return true;
    }

    private static bool TryParseQuotedChar(
        string line,
        string label,
        string validSeparators,
        bool required,
        out char value)
    {
        value = '\0';
        var pattern = "# " + Regex.Escape(label) + ": \"(?<sep>[" + Regex.Escape(validSeparators) + "]?)\"";
        var match = Regex.Match(line, pattern);
        if (!match.Success)
        {
            return false;
        }

        var text = match.Groups["sep"].Value;
        if (text.Length == 0)
        {
            return !required;
        }

        value = text[0];
        return true;
    }

    private static bool TryParseTimeFormat(string line, out int timeFormat)
    {
        timeFormat = 0;
        var match = Regex.Match(line, "# Time format: (?<format>\\d)");
        if (!match.Success)
        {
            return false;
        }

        timeFormat = match.Groups["format"].Value[0] - '0';
        return true;
    }

    private static string BuildPattern(ClxHeader header)
    {
        var pattern = string.Empty;
        if (header.Fields.Contains("Timestamp", StringComparer.Ordinal))
        {
            switch (header.TimeFormat)
            {
                case 6:
                    pattern += "(?<year>\\d\\d\\d\\d)";
                    if (header.DateSeparator != '\0')
                    {
                        pattern += header.DateSeparator;
                    }

                    goto case 5;
                case 5:
                    pattern += "(?<month>\\d\\d)";
                    if (header.DateSeparator != '\0')
                    {
                        pattern += header.DateSeparator;
                    }

                    goto case 4;
                case 4:
                    pattern += "(?<day>\\d\\d)";
                    if (header.TimeDateSeparator != '\0')
                    {
                        pattern += header.TimeDateSeparator;
                    }

                    goto case 3;
                case 3:
                    pattern += "(?<hours>\\d\\d)";
                    if (header.TimeSeparator != '\0')
                    {
                        pattern += header.TimeSeparator;
                    }

                    goto case 2;
                case 2:
                    pattern += "(?<minutes>\\d\\d)";
                    if (header.TimeSeparator != '\0')
                    {
                        pattern += header.TimeSeparator;
                    }

                    goto case 1;
                case 1:
                    pattern += "(?<seconds>\\d\\d)";
                    if (header.TimeSeparatorMs != '\0')
                    {
                        pattern += header.TimeSeparatorMs;
                    }

                    goto case 0;
                case 0:
                    pattern += "(?<ms>\\d\\d\\d)";
                    break;
                default:
                    return string.Empty;
            }

            pattern += Regex.Escape(header.ValueSeparator.ToString());
        }

        if (header.Fields.Contains("Type", StringComparer.Ordinal))
        {
            pattern += "(?<type>\\d)" + Regex.Escape(header.ValueSeparator.ToString());
        }

        if (header.Fields.Contains("ID", StringComparer.Ordinal))
        {
            pattern += "(?<id>[a-fA-F\\d]{1,8})" + Regex.Escape(header.ValueSeparator.ToString());
        }

        if (header.Fields.Contains("Length", StringComparer.Ordinal))
        {
            pattern += "(?<length>\\d)" + Regex.Escape(header.ValueSeparator.ToString());
        }

        if (header.Fields.Contains("Data", StringComparer.Ordinal))
        {
            pattern += "(?<data>([a-fA-F\\d]{2}){0,64})";
        }

        return pattern;
    }
}
