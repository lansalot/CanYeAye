using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCAN.Connections;
using WinCAN.Core;
using WinCAN.Core.Analysis;
using WinCAN.Core.Formatting;

namespace WinCAN.App.ViewModels;

/// <summary>One selectable frame ID in the flow view (always the full capture, ignoring filters).</summary>
public sealed class FlowIdItem(uint id, bool extended, string label)
{
    public uint Id { get; } = id;

    public bool Extended { get; } = extended;

    public string Label { get; } = label;
}

/// <summary>Per-byte row of the flow view table.</summary>
public sealed partial class FlowByteRow : ObservableObject
{
    private readonly Action<FlowByteRow> _referenceChanged;
    private readonly Action<FlowByteRow> _seekChanged;
    private readonly Action<FlowByteRow> _graphChanged;

    public FlowByteRow(
        int index,
        IBrush colour,
        string valueText,
        string referenceText,
        string seekText,
        Action<FlowByteRow> referenceChanged,
        Action<FlowByteRow> seekChanged,
        Action<FlowByteRow> graphChanged)
    {
        Index = index;
        Colour = colour;
        _valueText = valueText;
        _referenceText = referenceText;
        _seekText = seekText;
        _referenceChanged = referenceChanged;
        _seekChanged = seekChanged;
        _graphChanged = graphChanged;
    }

    public int Index { get; }

    public string IndexText => Index.ToString(CultureInfo.InvariantCulture);

    public IBrush Colour { get; }

    [ObservableProperty]
    private string _valueText;

    [ObservableProperty]
    private string _referenceText;

    [ObservableProperty]
    private string _seekText;

    [ObservableProperty]
    private bool _isGraphEnabled = true;

    partial void OnReferenceTextChanged(string value) => _referenceChanged(this);

    partial void OnSeekTextChanged(string value) => _seekChanged(this);

    partial void OnIsGraphEnabledChanged(bool value) => _graphChanged(this);
}

/// <summary>Immutable snapshot passed to <c>FlowBitGrid</c> (one per refresh).</summary>
public sealed class FlowBitGridModel
{
    public required byte[] Current { get; init; }

    public required byte[] Reference { get; init; }

    public required HashSet<int> Triggers { get; init; }

    public int ByteCount { get; init; }

    /// <summary>How long changed cells keep their red/green colour; 0 keeps them until the next frame.</summary>
    public int FlashDurationMs { get; init; }

    public long Sequence { get; init; }
}

/// <summary>One plotted payload byte.</summary>
public sealed class FlowGraphSeries(int byteIndex, IBrush brush, IReadOnlyList<double> values)
{
    public int ByteIndex { get; } = byteIndex;

    public IBrush Brush { get; } = brush;

    public IReadOnlyList<double> Values { get; } = values;
}

/// <summary>Immutable snapshot passed to <c>FlowGraphControl</c>.</summary>
public sealed class FlowGraphModel
{
    public IReadOnlyList<FlowGraphSeries> Series { get; init; } = [];

    public IReadOnlyList<double> Times { get; init; } = [];

    public bool UseTime { get; init; }

    public int Start { get; init; }

    public int End { get; init; }

    public int Current { get; init; }

    public bool HasFrames => End >= Start;
}

/// <summary>
/// Flow view window (re/flowviewwindow): pick an ID, step or play through its frames, watch the
/// payload bits change and graph every data byte over time. The ID list always covers the whole
/// capture and ignores the main window's filters.
/// </summary>
public sealed partial class FlowViewWindowViewModel : ObservableObject, IDisposable
{
    private const int MaxBytes = FlowViewSession.MaxBytes;

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

    private readonly Func<IReadOnlyList<CanFrame>> _capture;
    private readonly Func<CanFrame, string> _label;
    private readonly CanConnectionManager _manager;
    private readonly FlowViewSession _session = new();
    private readonly FlowByteRow?[] _rows = new FlowByteRow?[MaxBytes];
    private readonly List<double>?[] _series = new List<double>?[MaxBytes];
    private readonly List<double> _times = [];
    private readonly ConcurrentQueue<CanFrame> _liveQueue = new();
    private readonly DispatcherTimer _playbackTimer;
    private readonly DispatcherTimer _liveTimer;

    private bool _forward = true;
    private bool _playing;
    private bool _updating;
    private bool _suppressRowCallbacks;
    private long _sequence;
    private string _idStatus = string.Empty;
    private bool _disposed;

    public FlowViewWindowViewModel(
        Func<IReadOnlyList<CanFrame>> capture,
        Func<CanFrame, string> label,
        CanConnectionManager manager)
    {
        _capture = capture;
        _label = label;
        _manager = manager;

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        _playbackTimer.Tick += (_, _) => OnPlaybackTick();

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _liveTimer.Tick += (_, _) => DrainLiveQueue();
        _liveTimer.Start();

        _manager.FrameReceived += OnFrameReceived;

        ReloadCapture();
    }

    /// <summary>Every ID in the capture (filters are deliberately ignored here).</summary>
    public ObservableCollection<FlowIdItem> Ids { get; } = [];

    /// <summary>One row per payload byte of the selected ID.</summary>
    public ObservableCollection<FlowByteRow> ByteRows { get; } = [];

    [ObservableProperty]
    private FlowIdItem? _selectedIdItem;

    [ObservableProperty]
    private FlowBitGridModel? _bitGrid;

    [ObservableProperty]
    private FlowGraphModel? _graph;

    [ObservableProperty]
    private string _currentFrameText = "0 of 0";

    [ObservableProperty]
    private string _frameCountText = "0 frames";

    [ObservableProperty]
    private string _playbackText = "Paused";

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private double _timelinePosition;

    [ObservableProperty]
    private double _timelineMaximum;

    [ObservableProperty]
    private decimal _playbackSpeedMs = 25;

    [ObservableProperty]
    private bool _loopPlayback;

    [ObservableProperty]
    private bool _liveMode;

    [ObservableProperty]
    private bool _autoReference = true;

    [ObservableProperty]
    private bool _graphByTimestamp;

    [ObservableProperty]
    private double _chartRange = 50;

    [ObservableProperty]
    private int _triggerVersion;

    public string ChartRangeText => ((int)Math.Round(ChartRange)).ToString(CultureInfo.InvariantCulture);

    partial void OnSelectedIdItemChanged(FlowIdItem? value)
    {
        if (!_updating && value is not null)
        {
            SelectId(value.Id);
        }
    }

    partial void OnTimelinePositionChanged(double value)
    {
        if (_updating)
        {
            return;
        }

        Pause();
        if (LiveMode)
        {
            LiveMode = false;
        }

        MoveTo((int)Math.Round(value));
    }

    partial void OnPlaybackSpeedMsChanged(decimal value)
    {
        var ms = Math.Clamp(value, 0, 5000);
        _playbackTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, (double)ms));
    }

    partial void OnLiveModeChanged(bool value)
    {
        if (value)
        {
            Pause();
            if (_session.FrameCount > 0)
            {
                MoveTo(_session.FrameCount - 1);
            }
        }
    }

    partial void OnAutoReferenceChanged(bool value) => _session.AutoReference = value;

    partial void OnGraphByTimestampChanged(bool value) => RefreshGraph();

    partial void OnChartRangeChanged(double value)
    {
        OnPropertyChanged(nameof(ChartRangeText));
        RefreshGraph();
    }

    /// <summary>Re-reads the capture (also used when the main window loads or clears a log).</summary>
    public void ReloadCapture()
    {
        Pause();
        _session.Reset(_capture());
        RebuildIdList();
        StatusText = Ids.Count == 0 ? "No frames loaded." : $"{Ids.Count} frame IDs in the capture.";
        SelectFirstId();
    }

    /// <summary>R shortcut / toolbar: start playing forward, or pause when already playing.</summary>
    public void TogglePlayback()
    {
        if (_playing)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    [RelayCommand]
    private void Play()
    {
        if (_session.FrameCount == 0)
        {
            return;
        }

        if (LiveMode)
        {
            LiveMode = false;
        }

        _playing = true;
        _forward = true;
        PlaybackText = "Playing forward";
        _playbackTimer.Start();
    }

    [RelayCommand]
    private void Reverse()
    {
        if (_session.FrameCount == 0)
        {
            return;
        }

        if (LiveMode)
        {
            LiveMode = false;
        }

        _playing = true;
        _forward = false;
        PlaybackText = "Playing backward";
        _playbackTimer.Start();
    }

    [RelayCommand]
    private void Pause()
    {
        _playing = false;
        _playbackTimer.Stop();
        PlaybackText = "Paused";
    }

    [RelayCommand]
    private void Stop()
    {
        Pause();
        if (LiveMode)
        {
            LiveMode = false;
        }

        MoveTo(0);
    }

    [RelayCommand]
    private void StepBack()
    {
        Pause();
        if (LiveMode)
        {
            LiveMode = false;
        }

        MoveBy(-1);
    }

    [RelayCommand]
    private void StepForward()
    {
        Pause();
        if (LiveMode)
        {
            LiveMode = false;
        }

        MoveBy(1);
    }

    [RelayCommand]
    private void SelectAllGraphs() => SetAllGraphs(true);

    [RelayCommand]
    private void DeselectAllGraphs() => SetAllGraphs(false);

    /// <summary>Clicking a bit in the grid makes it a playback trigger (click again to clear).</summary>
    public void ToggleTrigger(int bit)
    {
        var active = _session.ToggleTrigger(bit);
        if (BitGrid is { } grid)
        {
            if (active)
            {
                grid.Triggers.Add(bit);
            }
            else
            {
                grid.Triggers.Remove(bit);
            }

            TriggerVersion++;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _playbackTimer.Stop();
        _liveTimer.Stop();
        _manager.FrameReceived -= OnFrameReceived;
    }

    private void OnFrameReceived(object? sender, CanFrame frame)
    {
        if (!_disposed)
        {
            _liveQueue.Enqueue(frame);
        }
    }

    private void DrainLiveQueue()
    {
        if (_liveQueue.IsEmpty)
        {
            return;
        }

        var newIds = false;
        var selectedFrames = false;
        while (_liveQueue.TryDequeue(out var frame))
        {
            var result = _session.Append(frame);
            newIds |= result.IsNewId;
            selectedFrames |= result.IsSelectedId;
        }

        if (newIds)
        {
            RebuildIdList(preserveSelection: true);
        }

        if (_session.SelectedId is null)
        {
            SelectFirstId();
            return;
        }

        if (!selectedFrames)
        {
            return;
        }

        for (var i = ByteRows.Count; i < _session.ByteCount; i++)
        {
            AddByteRow(i);
        }

        ExtendGraphSeries();

        if (LiveMode)
        {
            _session.MoveTo(_session.FrameCount - 1);
            UpdateAfterMove();
        }
        else
        {
            RefreshGraph();
            UpdateFrameLabels();
        }
    }

    private void SelectFirstId()
    {
        if (Ids.Count == 0)
        {
            ClearSelection();
            return;
        }

        if (SelectedIdItem is null)
        {
            SelectedIdItem = Ids[0];
        }
        else
        {
            SelectId(SelectedIdItem.Id);
        }
    }

    private void ClearSelection()
    {
        _updating = true;
        SelectedIdItem = null;
        _updating = false;
        ByteRows.Clear();
        Array.Clear(_rows);
        ClearGraphSeries();
        BitGrid = null;
        Graph = null;
        TimelineMaximum = 0;
        TimelinePosition = 0;
        CurrentFrameText = "0 of 0";
        FrameCountText = "0 frames";
    }

    private void RebuildIdList(bool preserveSelection = false)
    {
        var selectedId = preserveSelection ? SelectedIdItem?.Id : null;
        var firstFrames = new Dictionary<uint, CanFrame>();
        foreach (var frame in _session.AllFrames)
        {
            firstFrames.TryAdd(frame.FrameId, frame);
        }

        var items = firstFrames.Values
            .Select(frame => new FlowIdItem(
                frame.FrameId,
                frame.IsExtended,
                _label(frame)))
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id)
            .ToList();

        _updating = true;
        Ids.Clear();
        foreach (var item in items)
        {
            Ids.Add(item);
        }

        if (selectedId is { } id)
        {
            SelectedIdItem = Ids.FirstOrDefault(item => item.Id == id);
        }
        else
        {
            SelectedIdItem = null;
        }

        _updating = false;
    }

    private void SelectId(uint id)
    {
        Pause();
        _session.Select(id);
        _session.AutoReference = AutoReference;

        ByteRows.Clear();
        Array.Clear(_rows);
        ClearGraphSeries();

        for (var i = 0; i < _session.ByteCount; i++)
        {
            AddByteRow(i);
        }

        _updating = true;
        TimelineMaximum = Math.Max(0, _session.FrameCount - 1);
        TimelinePosition = 0;
        _updating = false;

        StatusText = _session.FrameCount == 0
            ? "No frames for this ID."
            : $"ID {FormatId(_session.SelectedId ?? id, SelectedIdItem?.Extended ?? false)}: {_session.FrameCount:N0} frames, {_session.ByteCount} bytes.";
        _idStatus = StatusText;

        RefreshAll();
    }

    private void AddByteRow(int i)
    {
        var row = new FlowByteRow(
            i,
            SeriesBrushes[i % SeriesBrushes.Length],
            Hex(_session.CurrentBytes[i]),
            Hex(_session.ReferenceBytes[i]),
            SeekText(_session.GetSeek(i)),
            OnReferenceChanged,
            OnSeekChanged,
            OnGraphChanged);
        _rows[i] = row;
        ByteRows.Add(row);
        BuildGraphSeries(i);
    }

    private void MoveBy(int delta)
    {
        if (!_session.Step(delta, LoopPlayback))
        {
            return;
        }

        UpdateAfterMove();
    }

    private void MoveTo(int position)
    {
        if (!_session.MoveTo(position))
        {
            return;
        }

        UpdateAfterMove();
    }

    private void UpdateAfterMove()
    {
        _updating = true;
        TimelinePosition = _session.Position;
        _updating = false;

        _suppressRowCallbacks = true;
        for (var i = 0; i < ByteRows.Count; i++)
        {
            ByteRows[i].ValueText = Hex(_session.CurrentBytes[i]);
            ByteRows[i].ReferenceText = Hex(_session.ReferenceBytes[i]);
        }

        _suppressRowCallbacks = false;
        RefreshAll();

        if (_session.SeekHit)
        {
            var byteIndex = MatchingSeekByte();
            StatusText = byteIndex >= 0
                ? $"Stopped: byte {byteIndex} matched seek value 0x{_session.CurrentBytes[byteIndex]:X2}."
                : "Stopped at a seek value.";
        }
        else if (_session.TriggerBitChanged)
        {
            StatusText = "Stopped: a selected bit changed.";
        }
        else
        {
            StatusText = _idStatus;
        }
    }

    private void RefreshAll()
    {
        RefreshBitGrid();
        RefreshGraph();
        UpdateFrameLabels();
    }

    private void UpdateFrameLabels()
    {
        _updating = true;
        TimelineMaximum = Math.Max(0, _session.FrameCount - 1);
        _updating = false;
        CurrentFrameText = _session.FrameCount == 0
            ? "0 of 0"
            : $"{_session.Position + 1} of {_session.FrameCount}";
        FrameCountText = $"{_session.FrameCount:N0} frames";
    }

    private void RefreshBitGrid()
    {
        if (_session.ByteCount <= 0)
        {
            BitGrid = null;
            return;
        }

        BitGrid = new FlowBitGridModel
        {
            Current = _session.CurrentBytes.ToArray(),
            Reference = _session.ReferenceBytes.ToArray(),
            Triggers = new HashSet<int>(_session.Triggers),
            ByteCount = _session.ByteCount,
            FlashDurationMs = AutoReference ? 900 : 0,
            Sequence = ++_sequence,
        };
    }

    private void RefreshGraph()
    {
        if (_session.FrameCount == 0)
        {
            Graph = null;
            return;
        }

        var range = Math.Max(1, (int)Math.Round(ChartRange));
        var start = Math.Max(0, _session.Position - range);
        var end = Math.Min(_session.FrameCount - 1, _session.Position + range);

        var series = new List<FlowGraphSeries>();
        for (var i = 0; i < _session.ByteCount; i++)
        {
            if (_rows[i] is { IsGraphEnabled: true } && _series[i] is { } values)
            {
                series.Add(new FlowGraphSeries(i, SeriesBrushes[i % SeriesBrushes.Length], values));
            }
        }

        Graph = new FlowGraphModel
        {
            Series = series,
            Times = _times,
            UseTime = GraphByTimestamp,
            Start = start,
            End = end,
            Current = _session.Position,
        };
    }

    private void OnPlaybackTick()
    {
        if (!_playing)
        {
            _playbackTimer.Stop();
            return;
        }

        if (LiveMode)
        {
            Pause();
            return;
        }

        if (!_session.Step(_forward ? 1 : -1, LoopPlayback))
        {
            Pause();
            return;
        }

        UpdateAfterMove();

        if (_session.SeekHit || _session.TriggerBitChanged)
        {
            Pause();
        }
    }

    private void OnReferenceChanged(FlowByteRow row)
    {
        if (_suppressRowCallbacks)
        {
            return;
        }

        if (TryParseHex(row.ReferenceText, out var value) && value is >= 0 and <= 0xFF)
        {
            _session.SetReferenceByte(row.Index, (byte)value);
            RefreshBitGrid();
        }
    }

    private void OnSeekChanged(FlowByteRow row)
    {
        if (_suppressRowCallbacks)
        {
            return;
        }

        if (!TryParseHex(row.SeekText, out var value))
        {
            return;
        }

        if (value is < -1 or > 0xFF)
        {
            return;
        }

        _session.SetSeek(row.Index, value);
    }

    private void OnGraphChanged(FlowByteRow row)
    {
        if (row.IsGraphEnabled)
        {
            BuildGraphSeries(row.Index);
        }
        else
        {
            _series[row.Index] = null;
        }

        RefreshGraph();
    }

    private void SetAllGraphs(bool enabled)
    {
        foreach (var row in ByteRows)
        {
            row.IsGraphEnabled = enabled;
        }
    }

    private void BuildGraphSeries(int byteIndex)
    {
        if (byteIndex < 0 || byteIndex >= MaxBytes)
        {
            return;
        }

        var values = new List<double>(_session.FrameCount);
        for (var i = 0; i < _session.FrameCount; i++)
        {
            var payload = _session.Frames[i].Payload;
            values.Add(byteIndex < payload.Length ? payload[byteIndex] : 0);
        }

        _series[byteIndex] = values;

        if (_times.Count != _session.FrameCount)
        {
            _times.Clear();
            foreach (var frame in _session.Frames)
            {
                _times.Add(frame.Timestamp.ToSeconds());
            }
        }
    }

    /// <summary>Appends newly arrived live frames to the timestamps and enabled byte series.</summary>
    private void ExtendGraphSeries()
    {
        for (var i = _times.Count; i < _session.FrameCount; i++)
        {
            _times.Add(_session.Frames[i].Timestamp.ToSeconds());
        }

        for (var b = 0; b < MaxBytes; b++)
        {
            if (_series[b] is not { } values)
            {
                continue;
            }

            for (var i = values.Count; i < _session.FrameCount; i++)
            {
                var payload = _session.Frames[i].Payload;
                values.Add(b < payload.Length ? payload[b] : 0);
            }
        }
    }

    private void ClearGraphSeries()
    {
        Array.Clear(_series);
        _times.Clear();
    }

    private int MatchingSeekByte()
    {
        for (var i = 0; i < _session.ByteCount; i++)
        {
            if (_session.GetSeek(i) >= 0 && _session.CurrentBytes[i] == _session.GetSeek(i))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Hex(byte value) => value.ToString("X2", CultureInfo.InvariantCulture);

    private static string SeekText(int value) => value < 0 ? "-1" : Hex((byte)value);

    private static string FormatId(uint id, bool extended = false) => "0x" + CanIdParser.FormatId(id, extended);

    /// <summary>Parses hex text (with or without 0x) plus SavvyCAN's -1 "disabled" value.</summary>
    internal static bool TryParseHex(string? text, out int value)
    {
        value = -1;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed == "-1")
        {
            return true;
        }

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        if (trimmed.Length == 0)
        {
            return false;
        }

        return int.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
