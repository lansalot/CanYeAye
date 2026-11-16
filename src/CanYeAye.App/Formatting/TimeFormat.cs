using System.Globalization;
using CanYeAye.Core;

namespace CanYeAye.App.Formatting;

/// <summary>Display formatting for frame timestamps (seconds + microseconds since the epoch).</summary>
public static class TimeFormat
{
    // DateTimeOffset.FromUnixTimeSeconds supports this range.
    private const long MinSeconds = -62_135_596_800L;
    private const long MaxSeconds = 253_402_300_799L;

    /// <summary>Local wall-clock time with millisecond precision, e.g. <c>11:08:11.603</c>.</summary>
    public static string LocalTime(FrameTimestamp timestamp)
    {
        if (timestamp.Seconds < MinSeconds || timestamp.Seconds > MaxSeconds)
        {
            return timestamp.ToString();
        }

        var local = DateTimeOffset.FromUnixTimeSeconds(timestamp.Seconds)
            .ToLocalTime()
            .AddTicks(timestamp.Microseconds * 10);
        return local.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }
}
