using System.Globalization;
using System.Text;

namespace CanYeAye.Core.FileFormats;

/// <summary>
/// String/number helpers with the exact semantics of the Qt helpers SavvyCAN's parsers rely on
/// (QByteArray::simplified, Utility::ParseStringToNum, Utility::unQuote, ...). Format parsers
/// depend on these quirks, so keep them faithful.
/// </summary>
public static class FormatHelpers
{
    /// <summary>QByteArray::simplified(): trim, then collapse internal whitespace runs to one space.</summary>
    public static string Simplified(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                if (builder.Length > 0)
                {
                    pendingSpace = true;
                }

                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>Split keeping empty entries (QString::split without SkipEmptyParts).</summary>
    public static string[] SplitKeepEmpty(string value, char separator) =>
        value.Split(separator);

    public static string[] SplitKeepEmpty(string value, string separator) =>
        value.Split(separator, StringSplitOptions.None);

    /// <summary>Split dropping empty entries, like QString::SkipEmptyParts.</summary>
    public static string[] SplitSkipEmpty(string value, char separator) =>
        value.Split(separator, StringSplitOptions.RemoveEmptyEntries);

    public static char FirstChar(string value) => value.Length == 0 ? '\0' : value[0];

    /// <summary>
    /// Utility::ParseStringToNum: "0x"/"X" prefix = hex, "0b"/"B" = binary, otherwise decimal.
    /// Garbage yields 0, never an exception.
    /// </summary>
    public static ulong ParseStringToNum(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return 0;
        }

        var text = input.Trim().ToUpperInvariant();
        if (text.Length == 0)
        {
            return 0;
        }

        if (text.StartsWith("0X", StringComparison.Ordinal) || text.StartsWith('X'))
        {
            var digits = text.StartsWith("0X", StringComparison.Ordinal) ? text[2..] : text[1..];
            return ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
                ? hex
                : 0;
        }

        if (text.StartsWith("0B", StringComparison.Ordinal) || text.StartsWith('B'))
        {
            var digits = text.StartsWith("0B", StringComparison.Ordinal) ? text[2..] : text[1..];
            ulong value = 0;
            foreach (var c in digits)
            {
                if (c == '1')
                {
                    value = (value << 1) | 1;
                }
                else if (c == '0')
                {
                    value <<= 1;
                }
                else
                {
                    return 0;
                }
            }

            return value;
        }

        return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : 0;
    }

    /// <summary>Utility::unQuote: return the text between the first pair of quotes, else the input.</summary>
    public static string UnQuote(string input)
    {
        var parts = input.Split('"');
        return parts.Length >= 3 ? parts[1] : input;
    }

    /// <summary>
    /// Utility::splitQuotedString: hand-rolled CSV split honouring double quotes; fields trimmed
    /// and unquoted. Empty fields are preserved.
    /// </summary>
    public static List<string> SplitQuotedString(string input)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in input)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(UnQuote(current.ToString()).Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(UnQuote(current.ToString()).Trim());
        return result;
    }

    /// <summary>Utility::GetTimeMS: milliseconds since local midnight.</summary>
    public static long MillisecondsSinceMidnight() =>
        DateTime.Now.TimeOfDay.Ticks / TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// Reads a text file as lines. SavvyCAN opens most formats in text mode, so CRLF and bare CR
    /// both terminate lines; <paramref name="rawNewlines"/> preserves CR-only splitting behaviour
    /// needed by CARBUS.
    /// </summary>
    public static string[] ReadLines(string path, bool rawNewlines = false, int maxLines = int.MaxValue)
    {
        var text = File.ReadAllText(path, Encoding.Latin1);
        if (rawNewlines)
        {
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        }

        var lines = text.Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }

        // Strip the CR left behind when the caller wants text-mode semantics but we split manually.
        if (!rawNewlines)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].EndsWith('\r'))
                {
                    lines[i] = lines[i][..^1];
                }
            }
        }

        return lines.Length > maxLines ? lines[..maxLines] : lines;
    }

    /// <summary>Parses a hex token, returning 0 for garbage (Qt's toInt(base) behaviour).</summary>
    public static ulong ParseHex(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return 0;
        }

        var text = input.Trim();
        return ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>Parses a decimal token, returning 0 for garbage.</summary>
    public static long ParseInt(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return 0;
        }

        return long.TryParse(input.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    public static double ParseDouble(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return 0;
        }

        return double.TryParse(input.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    /// <summary>Parses a two-character hex byte; invalid pairs become 0 like Qt's fromHex.</summary>
    public static byte ParseByte(string? input)
    {
        var value = ParseHex(input);
        return value > 0xFF ? (byte)0 : (byte)value;
    }

    /// <summary>
    /// Qt's QByteArray::toULongLong(): leading digits only, no sign. Returns 0 when nothing parses.
    /// </summary>
    public static ulong LeadingUnsigned(string input)
    {
        ulong value = 0;
        var any = false;
        foreach (var c in input)
        {
            if (c is < '0' or > '9')
            {
                break;
            }

            any = true;
            value = value * 10 + (ulong)(c - '0');
        }

        return any ? value : 0;
    }

    /// <summary>
    /// Qt's QByteArray::toLongLong() fallback used by the native CSV loader for signed timestamps.
    /// </summary>
    public static long LeadingSigned(string input)
    {
        var trimmed = input.TrimStart();
        var negative = false;
        if (trimmed.StartsWith('-'))
        {
            negative = true;
            trimmed = trimmed[1..];
        }
        else if (trimmed.StartsWith('+'))
        {
            trimmed = trimmed[1..];
        }

        var value = (long)LeadingUnsigned(trimmed);
        return negative ? -value : value;
    }

    public static string HexId(uint id, bool extended) =>
        extended ? id.ToString("X8", CultureInfo.InvariantCulture) : id.ToString("X3", CultureInfo.InvariantCulture);

    public static string Hex8(uint id) => id.ToString("X8", CultureInfo.InvariantCulture);
}
