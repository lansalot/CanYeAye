using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCAN.Connections;
using WinCAN.Core;
using WinCAN.Core.Sniffing;

namespace WinCAN.App.ViewModels;

/// <summary>One checkable ID entry in the sniffer's filter list.</summary>
public partial class SnifferFilterItem : ObservableObject
{
    private readonly Action<SnifferFilterItem> _changed;

    public SnifferFilterItem(uint id, Action<SnifferFilterItem> changed)
    {
        Id = id;
        Label = "0x" + id.ToString("X3");
        _changed = changed;
    }

    public uint Id { get; }

    public string Label { get; }

    [ObservableProperty]
    private bool _isChecked = true;

    partial void OnIsCheckedChanged(bool value) => _changed(this);
}

/// <summary>
/// Sniffer window (re/sniffer): one row per CAN ID showing delta, frequency and the live
/// payload with changed bytes highlighted. Supports notching, byte fading, bit view,
/// expiry, a minimum-frequency filter and per-ID include/exclude filters.
/// </summary>
public partial class SnifferWindowViewModel : ObservableObject, IDisposable
{
    private const int MinimumDataColumns = 8;

    private readonly CanConnectionManager _manager;
    private readonly Dictionary<uint, SnifferEntry> _entries = [];
    private readonly Dictionary<uint, SnifferFilterItem> _filters = [];
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _notchTimer;

    private uint _sequence;
    private bool _rebuildRows;

    public SnifferWindowViewModel(CanConnectionManager manager)
    {
        _manager = manager;
        _manager.FrameReceived += OnFrameReceived;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();

        _notchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds((double)NotchIntervalMs),
        };
        _notchTimer.Tick += (_, _) => RotateNotchWindow();
        _notchTimer.Start();
    }

    /// <summary>Rows currently displayed (filtered, sorted by ID).</summary>
    public ObservableCollection<SnifferEntry> Rows { get; } = [];

    /// <summary>Every ID seen so far, with its include/exclude checkbox.</summary>
    public ObservableCollection<SnifferFilterItem> Filters { get; } = [];

    [ObservableProperty]
    private bool _viewBits;

    [ObservableProperty]
    private bool _fadeInactiveBytes;

    [ObservableProperty]
    private bool _muteNotchedBits;

    [ObservableProperty]
    private bool _neverExpireIds;

    [ObservableProperty]
    private decimal _notchIntervalMs = 750;

    [ObservableProperty]
    private decimal _expireIntervalMs = 5000;

    /// <summary>Minimum observed rate (Hz) an ID must sustain to stay listed; 0 disables it.</summary>
    [ObservableProperty]
    private decimal _frequencyFilterHz;

    [ObservableProperty]
    private int _dataColumnCount = MinimumDataColumns;

    /// <summary>Incremented on every 200 ms refresh so the grid knows to repaint.</summary>
    [ObservableProperty]
    private int _refreshToken;

    public void Dispose()
    {
        _refreshTimer.Stop();
        _notchTimer.Stop();
        _manager.FrameReceived -= OnFrameReceived;
        Rows.Clear();
        Filters.Clear();
        _entries.Clear();
        _filters.Clear();
    }

    partial void OnNotchIntervalMsChanged(decimal value)
    {
        var ms = Math.Clamp((double)value, 100, 5000);
        _notchTimer.Interval = TimeSpan.FromMilliseconds(ms);
    }

    [RelayCommand]
    private void Notch()
    {
        foreach (var entry in Rows)
        {
            entry.Notch();
        }

        RefreshToken++;
    }

    [RelayCommand]
    private void Unnotch()
    {
        foreach (var entry in Rows)
        {
            entry.Unnotch();
        }

        RefreshToken++;
    }

    [RelayCommand]
    private void FilterAll() => SetAllFilters(true);

    [RelayCommand]
    private void FilterNone() => SetAllFilters(false);

    private void SetAllFilters(bool checkedState)
    {
        foreach (var filter in Filters)
        {
            filter.IsChecked = checkedState;
        }

        RebuildRows();
    }

    private void OnFrameReceived(object? sender, CanFrame frame) =>
        Dispatcher.UIThread.Post(() => ApplyFrame(frame));

    private void ApplyFrame(CanFrame frame)
    {
        var now = Environment.TickCount64;
        if (!_entries.TryGetValue(frame.FrameId, out var entry))
        {
            entry = new SnifferEntry(frame, now, _sequence);
            _entries[frame.FrameId] = entry;

            var filter = new SnifferFilterItem(frame.FrameId, OnFilterChanged);
            _filters[frame.FrameId] = filter;
            InsertFilter(filter);

            if (filter.IsChecked)
            {
                InsertRow(entry);
            }
        }
        else
        {
            entry.Update(frame, now, _sequence, MuteNotchedBits);
        }

        if (frame.Length > DataColumnCount)
        {
            DataColumnCount = Math.Min(SnifferEntry.MaxBytes, Math.Max(MinimumDataColumns, frame.Length));
        }
    }

    private void InsertRow(SnifferEntry entry)
    {
        if (Rows.Contains(entry))
        {
            return;
        }

        var index = 0;
        while (index < Rows.Count && Rows[index].Id < entry.Id)
        {
            index++;
        }

        Rows.Insert(index, entry);
    }

    private void InsertFilter(SnifferFilterItem filter)
    {
        var index = 0;
        while (index < Filters.Count && Filters[index].Id < filter.Id)
        {
            index++;
        }

        Filters.Insert(index, filter);
    }

    private void OnFilterChanged(SnifferFilterItem item)
    {
        if (_rebuildRows)
        {
            return;
        }

        if (item.IsChecked)
        {
            if (_entries.TryGetValue(item.Id, out var entry))
            {
                InsertRow(entry);
            }
        }
        else if (_entries.TryGetValue(item.Id, out var hidden))
        {
            Rows.Remove(hidden);
        }
    }

    private void RebuildRows()
    {
        _rebuildRows = true;
        Rows.Clear();
        foreach (var entry in _entries.Values.OrderBy(e => e.Id))
        {
            if (_filters.TryGetValue(entry.Id, out var filter) && filter.IsChecked)
            {
                Rows.Add(entry);
            }
        }

        _rebuildRows = false;
    }

    private void Refresh()
    {
        _sequence++;
        var now = Environment.TickCount64;
        var expireMs = (double)ExpireIntervalMs;
        var minimumRate = (double)FrequencyFilterHz;

        List<SnifferEntry>? expired = null;
        foreach (var entry in _entries.Values)
        {
            var age = entry.ElapsedMs(now);
            if (!NeverExpireIds && age > expireMs)
            {
                (expired ??= []).Add(entry);
                continue;
            }

            // Only judge the rate once a full second of history exists, so a newly seen
            // slow ID is not dropped before its rate can be measured.
            if (minimumRate > 0 &&
                now - entry.FirstSeenMs >= 1000 &&
                entry.RateHz(now) < minimumRate)
            {
                (expired ??= []).Add(entry);
            }
        }

        if (expired is not null)
        {
            foreach (var entry in expired)
            {
                RemoveEntry(entry);
            }
        }

        RefreshToken++;
    }

    private void RemoveEntry(SnifferEntry entry)
    {
        Rows.Remove(entry);
        _entries.Remove(entry.Id);
        if (_filters.Remove(entry.Id, out var filter))
        {
            Filters.Remove(filter);
        }
    }

    private void RotateNotchWindow()
    {
        foreach (var entry in _entries.Values)
        {
            entry.RotateMarker();
        }

        RefreshToken++;
    }
}
