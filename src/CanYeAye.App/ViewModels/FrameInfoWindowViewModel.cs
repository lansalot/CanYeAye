using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanYeAye.App.Services;
using CanYeAye.Core;
using CanYeAye.Core.Analysis;
using CanYeAye.Core.Dbc;

namespace CanYeAye.App.ViewModels;

/// <summary>One selectable frame ID in the Detailed Frame Information window.</summary>
public sealed class FrameInfoIdItem(uint id, bool extended, string label)
{
    public uint Id { get; } = id;

    public bool Extended { get; } = extended;

    public string Label { get; } = label;
}

/// <summary>Immutable heatmap snapshot: bit-change ratio for 64 bits, 0..1.</summary>
public sealed class FrameInfoHeatmapModel
{
    public required double[] Rates { get; init; }

    public int ByteCount { get; init; }
}

/// <summary>One labelled position on a bar chart's x axis.</summary>
public sealed record FrameInfoChartTick(int Index, string Text);

/// <summary>Immutable bar-chart snapshot (bit histogram or interval histogram).</summary>
public sealed class FrameInfoChartModel
{
    public required IReadOnlyList<double> Values { get; init; }

    public bool Logarithmic { get; init; }

    public IReadOnlyList<FrameInfoChartTick> Ticks { get; init; } = [];

    public string EmptyText { get; init; } = "No data";

    /// <summary>Optional axis caption drawn under the x-axis ticks.</summary>
    public string XAxisTitle { get; init; } = string.Empty;
}

/// <summary>Immutable snapshot of the eight per-byte value graphs.</summary>
public sealed class FrameInfoByteGraphsModel
{
    public IReadOnlyList<IReadOnlyList<double>> Series { get; init; } = [];

    public IReadOnlyList<IBrush> Brushes { get; init; } = [];

    public int FrameCount { get; init; }
}

/// <summary>
/// Detailed Frame Information window (re/frameinfowindow). Everything here is a snapshot of the
/// capture taken when the window opened - the window deliberately does not listen to live frames.
/// </summary>
public sealed partial class FrameInfoWindowViewModel : ObservableObject
{
    private static readonly IBrush[] SeriesBrushes =
    [
        new SolidColorBrush(Color.FromRgb(0x5B, 0x9B, 0xFF)),
        new SolidColorBrush(Color.FromRgb(0x4C, 0xD1, 0x64)),
        new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)),
        new SolidColorBrush(Color.FromRgb(0xF2, 0x5C, 0x5C)),
        new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
        new SolidColorBrush(Color.FromRgb(0xE0, 0xB4, 0x2E)),
        new SolidColorBrush(Color.FromRgb(0x35, 0xD0, 0xE0)),
        new SolidColorBrush(Color.FromRgb(0xD2, 0x82, 0xFF)),
    ];

    private readonly IReadOnlyList<CanFrame> _capture;
    private readonly Func<CanFrame, string> _label;
    private readonly DbcDatabase _dbc;
    private readonly IFileDialogService? _fileDialogs;

    private const string DefaultStatus =
        "Pick another ID on the left to analyse it; Save writes the details of every ID to a text file.";

    private const string NoFramesStatus =
        "No frames yet - load a log or capture some traffic, then reopen this window.";

    public FrameInfoWindowViewModel(
        IReadOnlyList<CanFrame> capture,
        Func<CanFrame, string> label,
        DbcDatabase dbc,
        IFileDialogService? fileDialogs = null)
    {
        _capture = capture;
        _label = label;
        _dbc = dbc;
        _fileDialogs = fileDialogs;

        BuildIdList();
        SnapshotText = capture.Count == 0
            ? "Snapshot taken when the window opened: no frames in the capture."
            : $"Snapshot taken when the window opened: {capture.Count:N0} frames, {Ids.Count:N0} unique IDs.";
        StatusText = Ids.Count == 0 ? NoFramesStatus : DefaultStatus;

        if (Ids.Count > 0)
        {
            SelectedIdItem = Ids[0];
        }
        else
        {
            ClearDetails();
        }
    }

    public ObservableCollection<FrameInfoIdItem> Ids { get; } = [];

    public ObservableCollection<FrameInfoDetailNode> DetailRoots { get; } = [];

    [ObservableProperty]
    private FrameInfoIdItem? _selectedIdItem;

    [ObservableProperty]
    private FrameInfoHeatmapModel? _heatmap;

    [ObservableProperty]
    private FrameInfoChartModel? _bitHistogram;

    [ObservableProperty]
    private FrameInfoChartModel? _intervalHistogram;

    [ObservableProperty]
    private FrameInfoByteGraphsModel? _byteGraphs;

    [ObservableProperty]
    private string _snapshotText = string.Empty;

    [ObservableProperty]
    private string _selectionText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public string UniqueIdText => $"({Ids.Count:N0} unique ids)";

    /// <summary>True when the capture has at least one analysable ID.</summary>
    public bool HasIds => Ids.Count > 0;

    partial void OnSelectedIdItemChanged(FrameInfoIdItem? value) => SelectId(value);

    [RelayCommand]
    private async Task SaveDetailsAsync()
    {
        if (_fileDialogs is null || Ids.Count == 0)
        {
            return;
        }

        var path = await _fileDialogs.PickSaveTextAsync("frame-details.txt");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            var builder = new StringBuilder();
            foreach (var item in Ids)
            {
                var report = Analyze(item);
                if (report is null)
                {
                    continue;
                }

                report.Root.AppendText(builder, 0);
                builder.Append('\n');
            }

            await File.WriteAllTextAsync(path, builder.ToString());
            StatusText = $"Saved details for {Ids.Count:N0} IDs to {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _fileDialogs.ShowMessageAsync("Save frame details", ex.Message);
        }
    }

    private void BuildIdList()
    {
        var firstFrames = new Dictionary<uint, CanFrame>();
        foreach (var frame in _capture)
        {
            firstFrames.TryAdd(frame.FrameId, frame);
        }

        var items = firstFrames.Values
            .Select(frame => new FrameInfoIdItem(frame.FrameId, frame.IsExtended, _label(frame)))
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id)
            .ToList();

        foreach (var item in items)
        {
            Ids.Add(item);
        }

        OnPropertyChanged(nameof(UniqueIdText));
        OnPropertyChanged(nameof(HasIds));
    }

    private FrameInfoReport? Analyze(FrameInfoIdItem item) =>
        FrameInfoAnalyzer.Analyze(
            _capture,
            item.Id,
            _dbc.FindMessageForFilter(item.Id, out _),
            item.Label);

    private void SelectId(FrameInfoIdItem? item)
    {
        if (item is null)
        {
            ClearDetails();
            return;
        }

        var report = Analyze(item);
        if (report is null)
        {
            ClearDetails();
            return;
        }

        DetailRoots.Clear();
        DetailRoots.Add(report.Root);
        Heatmap = new FrameInfoHeatmapModel
        {
            Rates = report.BitChangeRates,
            ByteCount = report.Bytes.Count,
        };
        BitHistogram = BuildBitHistogram(report);
        IntervalHistogram = BuildIntervalHistogram(report);
        ByteGraphs = BuildByteGraphs(report);

        var length = report.MinimumLength < report.MaximumLength
            ? $"{report.MinimumLength} to {report.MaximumLength} bytes"
            : $"{report.MinimumLength} bytes";
        SelectionText = $"{report.FrameCount:N0} frames, {length}, first {report.Bytes.Count} byte(s) analysed.";
        StatusText = DefaultStatus;
    }

    private void ClearDetails()
    {
        DetailRoots.Clear();
        Heatmap = null;
        BitHistogram = null;
        IntervalHistogram = null;
        ByteGraphs = null;
        SelectionText = "Select a frame ID to see its details.";
        StatusText = Ids.Count == 0 ? NoFramesStatus : DefaultStatus;
    }

    private static FrameInfoChartModel BuildBitHistogram(FrameInfoReport report)
    {
        var ticks = new List<FrameInfoChartTick>();
        for (var bit = 0; bit < FrameInfoAnalyzer.StatBytes * 8; bit += 8)
        {
            ticks.Add(new FrameInfoChartTick(bit, bit.ToString(CultureInfo.InvariantCulture)));
        }

        return new FrameInfoChartModel
        {
            Values = report.BitSetCounts.Select(count => (double)count).ToArray(),
            Logarithmic = true,
            Ticks = ticks,
            XAxisTitle = "Bits",
            EmptyText = "No bits set for this ID.",
        };
    }

    private static FrameInfoChartModel BuildIntervalHistogram(FrameInfoReport report)
    {
        var intervals = report.Intervals;
        if (!intervals.HasIntervals)
        {
            return new FrameInfoChartModel
            {
                Values = [],
                Logarithmic = true,
                EmptyText = "No inter-frame intervals (only one frame for this ID).",
            };
        }

        var values = intervals.HistogramCounts.Select(count => (double)count).ToArray();
        var ticks = new List<FrameInfoChartTick>();
        for (var index = 0; index < values.Length; index += 5)
        {
            var label = intervals.HistogramUpperMilliseconds[index]
                .ToString("0.#", CultureInfo.InvariantCulture);
            ticks.Add(new FrameInfoChartTick(index, label));
        }

        if (ticks[^1].Index != values.Length - 1)
        {
            var label = intervals.HistogramUpperMilliseconds[^1]
                .ToString("0.#", CultureInfo.InvariantCulture);
            ticks.Add(new FrameInfoChartTick(values.Length - 1, label));
        }

        return new FrameInfoChartModel
        {
            Values = values,
            Logarithmic = true,
            Ticks = ticks,
            XAxisTitle = "Interval (ms)",
        };
    }

    private static FrameInfoByteGraphsModel BuildByteGraphs(FrameInfoReport report)
    {
        var series = report.Bytes
            .Select(stats => (IReadOnlyList<double>)stats.Series.Select(value => (double)value).ToArray())
            .ToArray();

        return new FrameInfoByteGraphsModel
        {
            Series = series,
            Brushes = SeriesBrushes,
            FrameCount = report.FrameCount,
        };
    }
}
