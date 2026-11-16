using System.Globalization;

namespace CanYeAye.Core;

/// <summary>Numeric parsing helpers matching SavvyCAN's Utility::ParseStringToNum.</summary>
public static class CanIdParser
{
    /// <summary>
    /// Parses 0x/X-prefixed hex, 0b/B-prefixed binary, or decimal. Invalid text yields 0.
    /// </summary>
    public static ulong Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return 0;
        }

        var text = input.Trim().ToUpperInvariant();

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

    /// <summary>
    /// Parses a CAN ID that may be bare hexadecimal (the usual case in log files).
    /// Frames ending in 'x' are treated as extended by callers before this is used.
    /// </summary>
    public static uint ParseId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return 0;
        }

        var text = input.Trim().ToUpperInvariant();
        if (text.StartsWith("0X", StringComparison.Ordinal) || text.StartsWith('X'))
        {
            return (uint)Parse(text);
        }

        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
            ? hex
            : (uint)Parse(text);
    }

    public static string FormatId(uint id, bool extended) =>
        extended ? id.ToString("X8", CultureInfo.InvariantCulture) : id.ToString("X3", CultureInfo.InvariantCulture);
}
