using System.Globalization;
using System.Text;
using CanYeAye.Core.Dbc;
using CanYeAye.Core.Protocols;

namespace CanYeAye.Core.Analysis;

/// <summary>Statistics for one payload byte of the selected frame ID.</summary>
public sealed class FrameInfoByteStats
{
    internal FrameInfoByteStats(int index)
    {
        Index = index;
    }

    /// <summary>Zero-based payload byte index (0..7).</summary>
    public int Index { get; }

    /// <summary>Lowest value observed for this byte.</summary>
    public int Minimum { get; internal set; }

    /// <summary>Highest value observed for this byte.</summary>
    public int Maximum { get; internal set; }

    /// <summary>Bits that differ from the first frame's value at least once (SavvyCAN's changed mask).</summary>
    public byte ChangedBits { get; internal set; }

    /// <summary>How many frames carried each value (0..255).</summary>
    public int[] ValueCounts { get; } = new int[256];

    /// <summary>How many frames had each bit (bit 0 = LSB) set.</summary>
    public int[] BitSetCounts { get; } = new int[8];

    /// <summary>Bit transitions divided by the frame count (bit 0 = LSB).</summary>
    public double[] BitChangeRates { get; } = new double[8];

    /// <summary>Value of this byte in every frame of the ID.</summary>
    public byte[] Series { get; internal set; } = [];
}

/// <summary>Interval statistics and histogram for the selected frame ID.</summary>
public sealed class FrameInfoIntervalStats
{
    internal FrameInfoIntervalStats(
        long averageMicroseconds,
        long minimumMicroseconds,
        long maximumMicroseconds,
        long standardDeviationMicroseconds,
        long p90RangeMicroseconds,
        bool hasIntervals,
        int[] histogramCounts,
        double[] histogramUpperMilliseconds)
    {
        AverageMicroseconds = averageMicroseconds;
        MinimumMicroseconds = minimumMicroseconds;
        MaximumMicroseconds = maximumMicroseconds;
        StandardDeviationMicroseconds = standardDeviationMicroseconds;
        P90RangeMicroseconds = p90RangeMicroseconds;
        HasIntervals = hasIntervals;
        HistogramCounts = histogramCounts;
        HistogramUpperMilliseconds = histogramUpperMilliseconds;
    }

    public long AverageMicroseconds { get; }

    public long MinimumMicroseconds { get; }

    public long MaximumMicroseconds { get; }

    /// <summary>Maximum minus minimum interval.</summary>
    public long VariationMicroseconds => MaximumMicroseconds - MinimumMicroseconds;

    public long StandardDeviationMicroseconds { get; }

    /// <summary>Width of the range that holds 90% of the intervals (P95 - P5).</summary>
    public long P90RangeMicroseconds { get; }

    /// <summary>False when the ID appears only once (no inter-frame interval exists).</summary>
    public bool HasIntervals { get; }

    /// <summary>
    /// One count per histogram bin. The reference draws 21 step points (20 bins plus the
    /// minimum edge); the last entry therefore always holds the longest intervals.
    /// </summary>
    public int[] HistogramCounts { get; }

    /// <summary>Upper bound of each histogram bin in milliseconds (same length as the counts).</summary>
    public double[] HistogramUpperMilliseconds { get; }
}

/// <summary>One discrete value of a DBC signal and how often it occurred.</summary>
public sealed record FrameInfoValueCount(string Value, int Count);

/// <summary>Value histogram of one DBC signal across the selected ID's frames.</summary>
public sealed class FrameInfoSignalValues(string signalName, IReadOnlyList<FrameInfoValueCount> values)
{
    public string SignalName { get; } = signalName;

    public IReadOnlyList<FrameInfoValueCount> Values { get; } = values;
}

/// <summary>One line of the detail tree (text + nested children).</summary>
public sealed class FrameInfoDetailNode(string text)
{
    /// <summary>Line text shown in the tree and written by the text export.</summary>
    public string Text { get; } = text;

    /// <summary>Nested lines, in display order.</summary>
    public List<FrameInfoDetailNode> Children { get; } = [];

    /// <summary>Initial expansion state for the tree view.</summary>
    public bool IsExpanded { get; set; }

    public FrameInfoDetailNode Add(string text, bool expanded = false)
    {
        var child = new FrameInfoDetailNode(text) { IsExpanded = expanded };
        Children.Add(child);
        return child;
    }

    /// <summary>Tab-indented dump (the reference's Save details format).</summary>
    public void AppendText(StringBuilder builder, int indent)
    {
        builder.Append('\t', indent);
        builder.Append(Text);
        builder.Append('\n');
        foreach (var child in Children)
        {
            child.AppendText(builder, indent + 1);
        }
    }

    public string ToText()
    {
        var builder = new StringBuilder();
        AppendText(builder, 0);
        return builder.ToString();
    }
}

/// <summary>Snapshot analysis of one frame ID for the Detailed Frame Information window.</summary>
public sealed class FrameInfoReport
{
    internal FrameInfoReport(
        uint id,
        bool extended,
        string label,
        int frameCount,
        int minimumLength,
        int maximumLength,
        FrameInfoByteStats[] bytes,
        FrameInfoIntervalStats intervals,
        int[] bitSetCounts,
        double[] bitChangeRates,
        IReadOnlyList<FrameInfoSignalValues> signals,
        FrameInfoDetailNode root)
    {
        Id = id;
        Extended = extended;
        Label = label;
        FrameCount = frameCount;
        MinimumLength = minimumLength;
        MaximumLength = maximumLength;
        Bytes = bytes;
        Intervals = intervals;
        BitSetCounts = bitSetCounts;
        BitChangeRates = bitChangeRates;
        Signals = signals;
        Root = root;
    }

    public uint Id { get; }

    public bool Extended { get; }

    /// <summary>Display label used in the ID list (hex ID plus optional names).</summary>
    public string Label { get; }

    public int FrameCount { get; }

    public int MinimumLength { get; }

    public int MaximumLength { get; }

    /// <summary>Statistics of the first (at most) eight payload bytes.</summary>
    public IReadOnlyList<FrameInfoByteStats> Bytes { get; }

    public FrameInfoIntervalStats Intervals { get; }

    /// <summary>Number of frames with each bit set (64 entries, bit = byte * 8 + bit index).</summary>
    public int[] BitSetCounts { get; }

    /// <summary>Bit transitions divided by the frame count (64 entries).</summary>
    public double[] BitChangeRates { get; }

    public IReadOnlyList<FrameInfoSignalValues> Signals { get; }

    /// <summary>Tree shared by the detail view and the saved text.</summary>
    public FrameInfoDetailNode Root { get; }

    /// <summary>Tab-indented text for this ID (the reference's Save details format).</summary>
    public string ToText() => Root.ToText();
}

/// <summary>
/// Snapshot statistics for the Detailed Frame Information window (re/frameinfowindow).
/// Operates on the capture as it was when the window opened; it never observes live frames.
/// </summary>
/// <remarks>
/// Deviations from the reference:
/// <list type="bullet">
/// <item>Inter-frame intervals subtract whole seconds + microseconds instead of only the
/// microsecond field, so captures that cross a second boundary are measured correctly.</item>
/// <item>A single-frame ID reports zero interval statistics instead of the reference's
/// <c>INT_MAX / 1000</c> minimum.</item>
/// <item>J1939 / GMLAN / histogram sections are real tree nodes instead of space-indented
/// siblings, so the text export gets one tab per level.</item>
/// </list>
/// </remarks>
public static class FrameInfoAnalyzer
{
    /// <summary>Per-byte statistics cover the first 8 bytes; CAN FD / FlexRay frames can be longer.</summary>
    public const int StatBytes = 8;

    /// <summary>Number of histogram bins used for the interval histogram.</summary>
    public const int IntervalHistogramBars = 20;

    /// <summary>Every unique ID in the capture, ascending.</summary>
    public static IReadOnlyList<uint> CollectIds(IEnumerable<CanFrame> frames)
    {
        var ids = new HashSet<uint>();
        foreach (var frame in frames)
        {
            ids.Add(frame.FrameId);
        }

        var list = ids.ToList();
        list.Sort();
        return list;
    }

    /// <summary>
    /// Builds the report for <paramref name="id"/>, or null when the capture has no frame with it.
    /// <paramref name="message"/> is the DBC message used for the signal value histograms.
    /// </summary>
    public static FrameInfoReport? Analyze(
        IReadOnlyList<CanFrame> frames,
        uint id,
        DbcMessage? message = null,
        string? label = null)
    {
        var matching = new List<CanFrame>();
        foreach (var frame in frames)
        {
            if (frame.FrameId == id)
            {
                matching.Add(frame);
            }
        }

        if (matching.Count == 0)
        {
            return null;
        }

        var extended = matching[0].IsExtended;
        var displayLabel = string.IsNullOrWhiteSpace(label) ? FormatId(id, extended) : label!;
        var byteCount = 0;
        foreach (var frame in matching)
        {
            byteCount = Math.Max(byteCount, Math.Min(frame.Payload.Length, StatBytes));
        }

        var bytes = new FrameInfoByteStats[byteCount];
        for (var i = 0; i < byteCount; i++)
        {
            bytes[i] = new FrameInfoByteStats(i) { Minimum = 256, Maximum = -1, Series = new byte[matching.Count] };
        }

        var bitSetCounts = new int[StatBytes * 8];
        var bitChanges = new long[StatBytes * 8];
        var changedBits = new byte[StatBytes];
        var referenceBits = new byte[StatBytes];
        var previousByte = new byte[StatBytes];
        var minimumLength = int.MaxValue;
        var maximumLength = 0;

        var firstPayload = matching[0].Payload;
        for (var i = 0; i < Math.Min(firstPayload.Length, StatBytes); i++)
        {
            referenceBits[i] = firstPayload[i];
            previousByte[i] = firstPayload[i];
        }

        var intervals = new List<long>(Math.Max(0, matching.Count - 1));
        var intervalSum = 0L;
        var previousTimestamp = matching[0].Timestamp.ToMicroseconds();

        for (var frameIndex = 0; frameIndex < matching.Count; frameIndex++)
        {
            var frame = matching[frameIndex];
            var payload = frame.Payload;
            var length = payload.Length;

            if (frameIndex > 0)
            {
                var currentTimestamp = frame.Timestamp.ToMicroseconds();
                var interval = Math.Abs(currentTimestamp - previousTimestamp);
                intervals.Add(interval);
                intervalSum += interval;
                previousTimestamp = currentTimestamp;
            }

            minimumLength = Math.Min(minimumLength, length);
            maximumLength = Math.Max(maximumLength, length);

            var limit = Math.Min(length, byteCount);
            for (var byteIndex = 0; byteIndex < limit; byteIndex++)
            {
                var value = payload[byteIndex];
                var stats = bytes[byteIndex];
                stats.Series[frameIndex] = value;
                if (value < stats.Minimum)
                {
                    stats.Minimum = value;
                }

                if (value > stats.Maximum)
                {
                    stats.Maximum = value;
                }

                stats.ValueCounts[value]++;
                for (var bit = 0; bit < 8; bit++)
                {
                    if ((value & (1 << bit)) != 0)
                    {
                        stats.BitSetCounts[bit]++;
                        bitSetCounts[byteIndex * 8 + bit]++;
                    }
                }

                changedBits[byteIndex] |= (byte)(referenceBits[byteIndex] ^ value);

                if (previousByte[byteIndex] != value)
                {
                    var flipped = previousByte[byteIndex] ^ value;
                    for (var bit = 0; bit < 8; bit++)
                    {
                        if ((flipped & (1 << bit)) != 0)
                        {
                            bitChanges[byteIndex * 8 + bit]++;
                        }
                    }

                    previousByte[byteIndex] = value;
                }
            }
        }

        for (var byteIndex = 0; byteIndex < byteCount; byteIndex++)
        {
            bytes[byteIndex].ChangedBits = changedBits[byteIndex];
            for (var bit = 0; bit < 8; bit++)
            {
                bytes[byteIndex].BitChangeRates[bit] = bitChanges[byteIndex * 8 + bit] / (double)matching.Count;
            }
        }

        var bitChangeRates = new double[StatBytes * 8];
        for (var bit = 0; bit < bitChangeRates.Length; bit++)
        {
            bitChangeRates[bit] = bitChanges[bit] / (double)matching.Count;
        }

        var intervalStats = BuildIntervalStats(intervals, intervalSum);
        var signals = BuildSignalValues(matching, message);
        var root = BuildDetailTree(
            id,
            extended,
            displayLabel,
            matching.Count,
            minimumLength,
            maximumLength,
            bytes,
            intervalStats,
            bitSetCounts,
            bitChangeRates,
            signals);

        return new FrameInfoReport(
            id,
            extended,
            displayLabel,
            matching.Count,
            minimumLength,
            maximumLength,
            bytes,
            intervalStats,
            bitSetCounts,
            bitChangeRates,
            signals,
            root);
    }

    private static FrameInfoIntervalStats BuildIntervalStats(List<long> intervals, long intervalSum)
    {
        if (intervals.Count == 0)
        {
            return new FrameInfoIntervalStats(0, 0, 0, 0, 0, false, [], []);
        }

        var sorted = intervals.ToArray();
        Array.Sort(sorted);

        var mean = intervalSum / sorted.Length;
        var minimum = sorted[0];
        var maximum = sorted[^1];

        var variance = 0.0;
        foreach (var interval in sorted)
        {
            var delta = interval - mean;
            variance += delta * delta;
        }

        variance /= sorted.Length;
        var standardDeviation = (long)Math.Sqrt(variance);
        var p5 = sorted[(int)Math.Floor(0.05 * sorted.Length)];
        var p95 = sorted[(int)Math.Floor(0.95 * sorted.Length)];

        var step = (long)Math.Ceiling((maximum - minimum) / (double)IntervalHistogramBars);
        if (step <= 0)
        {
            step = 1;
        }

        var counts = new int[IntervalHistogramBars + 1];
        var upperBounds = new double[IntervalHistogramBars + 1];
        var index = 0;
        for (var bin = 0; bin <= IntervalHistogramBars; bin++)
        {
            var currentMax = maximum - ((IntervalHistogramBars - bin) * step);
            while (index < sorted.Length && sorted[index] <= currentMax)
            {
                counts[bin]++;
                index++;
            }

            upperBounds[bin] = currentMax / 1000.0;
        }

        return new FrameInfoIntervalStats(
            mean,
            minimum,
            maximum,
            standardDeviation,
            p95 - p5,
            true,
            counts,
            upperBounds);
    }

    private static IReadOnlyList<FrameInfoSignalValues> BuildSignalValues(
        IReadOnlyList<CanFrame> frames,
        DbcMessage? message)
    {
        if (message is null)
        {
            return [];
        }

        var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var frame in frames)
        {
            foreach (var signal in message.ActiveSignals(frame))
            {
                var text = signal.ProcessAsText(frame, outputName: false);
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                if (!counts.TryGetValue(signal.Name, out var values))
                {
                    values = new Dictionary<string, int>(StringComparer.Ordinal);
                    counts[signal.Name] = values;
                }

                values[text] = values.GetValueOrDefault(text) + 1;
            }
        }

        var result = new List<FrameInfoSignalValues>(counts.Count);
        foreach (var pair in counts.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var values = pair.Value
                .Select(entry => new FrameInfoValueCount(entry.Key, entry.Value))
                .OrderByDescending(entry => entry.Count)
                .ThenBy(entry => entry.Value, StringComparer.Ordinal)
                .ToArray();
            result.Add(new FrameInfoSignalValues(pair.Key, values));
        }

        return result;
    }

    private static FrameInfoDetailNode BuildDetailTree(
        uint id,
        bool extended,
        string label,
        int frameCount,
        int minimumLength,
        int maximumLength,
        IReadOnlyList<FrameInfoByteStats> bytes,
        FrameInfoIntervalStats intervals,
        IReadOnlyList<int> bitSetCounts,
        IReadOnlyList<double> bitChangeRates,
        IReadOnlyList<FrameInfoSignalValues> signals)
    {
        var root = new FrameInfoDetailNode($"ID: {label}") { IsExpanded = true };

        if (extended)
        {
            var j1939 = J1939Id.Decode(id);
            var j1939Node = root.Add("J1939 decoding", expanded: true);
            if (j1939.IsBroadcast)
            {
                j1939Node.Add("Broadcast Frame");
            }
            else
            {
                j1939Node.Add(j1939.Destination < 0xFF
                    ? $"Destination ID: {FormatNumber(j1939.Destination)}"
                    : "Destination ID: Global (ALL)");
            }

            j1939Node.Add($"Priority: {j1939.Priority} (0-7)");
            j1939Node.Add($"SRC: {FormatNumber(j1939.Source)}");
            j1939Node.Add($"EDP: {j1939.ExtendedDataPage}");
            j1939Node.Add($"DP: {j1939.DataPage}");
            j1939Node.Add($"PGN: {FormatNumber(j1939.Pgn)} ({j1939.Pgn})");
            j1939Node.Add($"PF: {FormatNumber(j1939.PduFormat)}");
            j1939Node.Add($"PS: {FormatNumber(j1939.PduSpecific)}");

            var gmlan = root.Add("GMLAN 29bit decoding", expanded: true);
            gmlan.Add($"Priority bits: {FormatNumber((int)((id >> 26) & 0x7))}");
            gmlan.Add($"Arbitration Id: {FormatNumber((int)((id >> 13) & 0x1FFF))}");
            gmlan.Add($"Sender Id: {FormatNumber((int)(id & 0x1FFF))}");
        }

        root.Add($"# of frames: {frameCount.ToString(CultureInfo.InvariantCulture)}");
        root.Add(minimumLength < maximumLength
            ? $"Data Length: {minimumLength} to {maximumLength}"
            : $"Data Length: {minimumLength}");

        root.Add($"Average inter-frame interval: {FormatMilliseconds(intervals.AverageMicroseconds)}ms");
        root.Add($"Minimum inter-frame interval: {FormatMilliseconds(intervals.MinimumMicroseconds)}ms");
        root.Add($"Maximum inter-frame interval: {FormatMilliseconds(intervals.MaximumMicroseconds)}ms");
        root.Add($"Inter-frame interval variation: {FormatMilliseconds(intervals.VariationMicroseconds)}ms");
        root.Add($"Interval standard deviation: {FormatMilliseconds(intervals.StandardDeviationMicroseconds)}ms");
        root.Add($"Minimum range to fit 90% of inter-frame intervals: {FormatMilliseconds(intervals.P90RangeMicroseconds)}ms");

        foreach (var stats in bytes)
        {
            var byteNode = root.Add($"Data Byte {stats.Index.ToString(CultureInfo.InvariantCulture)}", expanded: true);
            byteNode.Add(
                $"Changed bits: 0x{stats.ChangedBits.ToString("X2", CultureInfo.InvariantCulture)}  ({FormatByteAsBinary(stats.ChangedBits)})");
            byteNode.Add($"Range: {FormatNumber(stats.Minimum)} to {FormatNumber(stats.Maximum)}");

            var histogram = byteNode.Add("Histogram");
            for (var value = 0; value < stats.ValueCounts.Length; value++)
            {
                var count = stats.ValueCounts[value];
                if (count > 0)
                {
                    histogram.Add(
                        $"{value.ToString(CultureInfo.InvariantCulture)}/0x{value.ToString("X2", CultureInfo.InvariantCulture)} " +
                        $"({FormatByteAsBinary((byte)value)}) -> {count.ToString(CultureInfo.InvariantCulture)}");
                }
            }
        }

        var bitfield = root.Add("Bitfield Histogram");
        for (var bit = 0; bit < bitSetCounts.Count; bit++)
        {
            bitfield.Add(
                $"{bit.ToString(CultureInfo.InvariantCulture)} (Byte {bit / 8} Bit {bit % 8}) : " +
                $"{bitSetCounts[bit].ToString(CultureInfo.InvariantCulture)}");
        }

        var heatmap = root.Add("Bit Change Heatmap");
        for (var bit = 0; bit < bitChangeRates.Count; bit++)
        {
            heatmap.Add(
                $"{bit.ToString(CultureInfo.InvariantCulture)} (Byte {bit / 8} Bit {bit % 8}) : " +
                $"{FormatPercent(bitChangeRates[bit])}");
        }

        foreach (var signal in signals)
        {
            var signalNode = root.Add(signal.SignalName);
            foreach (var value in signal.Values)
            {
                signalNode.Add($"{value.Value}: {value.Count.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        return root;
    }

    private static string FormatMilliseconds(long microseconds) =>
        (microseconds / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatPercent(double ratio) =>
        (ratio * 100.0).ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatByteAsBinary(byte value)
    {
        Span<char> buffer = stackalloc char[8];
        for (var bit = 7; bit >= 0; bit--)
        {
            buffer[7 - bit] = (value & (1 << bit)) != 0 ? '1' : '0';
        }

        return new string(buffer);
    }

    /// <summary>SavvyCAN Utility::formatNumber (hex, width grows with the value).</summary>
    private static string FormatNumber(long value)
    {
        var unsigned = (ulong)value;
        if (unsigned < 256)
        {
            return "0x" + unsigned.ToString("X2", CultureInfo.InvariantCulture);
        }

        if (unsigned < 65536)
        {
            return "0x" + unsigned.ToString("X4", CultureInfo.InvariantCulture);
        }

        return unsigned < 4294967296
            ? "0x" + unsigned.ToString("X8", CultureInfo.InvariantCulture)
            : "0x" + unsigned.ToString("X16", CultureInfo.InvariantCulture);
    }

    private static string FormatId(uint id, bool extended) =>
        extended
            ? "0x" + id.ToString("X8", CultureInfo.InvariantCulture)
            : "0x" + (id & 0x7FF).ToString("X3", CultureInfo.InvariantCulture);
}
