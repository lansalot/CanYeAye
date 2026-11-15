using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCAN.App.Formatting;
using WinCAN.Connections;
using WinCAN.Core;
using WinCAN.Core.Formatting;

namespace WinCAN.App.ViewModels;

public partial class SnifferRow : ObservableObject
{
    public SnifferRow(byte bus, uint id, bool extended)
    {
        Bus = bus;
        Id = id;
        Extended = extended;
    }

    public byte Bus { get; }

    public uint Id { get; }

    public bool Extended { get; }

    public string IdText => CanIdParser.FormatId(Id, Extended);

    public string ExtendedText => Extended ? "EXT" : "STD";

    [ObservableProperty]
    private long _count;

    [ObservableProperty]
    private double _rate;

    [ObservableProperty]
    private string _data = string.Empty;

    [ObservableProperty]
    private string _type = string.Empty;

    [ObservableProperty]
    private string _lastSeen = string.Empty;

    internal long PendingRateCount;
}

/// <summary>
/// Sniffer: aggregates received frames per bus+ID with counts, live rate and the last payload,
/// the way SavvyCAN's sniffer window summarises traffic.
/// </summary>
public partial class SnifferWindowViewModel : ObservableObject, IDisposable
{
    private readonly CanConnectionManager _manager;
    private readonly Dictionary<(byte Bus, uint Id), SnifferRow> _rows = [];
    private readonly DispatcherTimer _rateTimer;
    private long _total;

    public SnifferWindowViewModel(CanConnectionManager manager)
    {
        _manager = manager;
        _manager.FrameReceived += OnFrameReceived;

        _rateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _rateTimer.Tick += (_, _) => UpdateRates();
        _rateTimer.Start();
    }

    public ObservableCollection<SnifferRow> Rows { get; } = [];

    [ObservableProperty]
    private bool _suspended;

    [ObservableProperty]
    private string _status = "0 frames";

    public void Dispose()
    {
        _rateTimer.Stop();
        _manager.FrameReceived -= OnFrameReceived;
    }

    [RelayCommand]
    private void Clear()
    {
        Rows.Clear();
        _rows.Clear();
        _total = 0;
        Status = "0 frames";
    }

    private void OnFrameReceived(object? sender, CanFrame frame)
    {
        if (Suspended)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => AddFrame(frame));
    }

    private void AddFrame(CanFrame frame)
    {
        _total++;
        var key = (frame.Bus, frame.FrameId);
        if (!_rows.TryGetValue(key, out var row))
        {
            row = new SnifferRow(frame.Bus, frame.FrameId, frame.IsExtended);
            _rows[key] = row;
            Rows.Add(row);
        }

        row.Count++;
        row.PendingRateCount++;
        row.Data = HexFormat.SpaceDelimited(frame.Payload);
        row.Type = frame.FrameType switch
        {
            FrameType.CanFdDataFrame => "FD",
            FrameType.RemoteRequestFrame => "RTR",
            FrameType.ErrorFrame => "ERR",
            _ => "CAN",
        };
        row.LastSeen = TimeFormat.LocalTime(frame.Timestamp);
        Status = $"{_total:N0} frames, {Rows.Count} IDs";
    }

    private void UpdateRates()
    {
        foreach (var row in Rows)
        {
            row.Rate = row.PendingRateCount;
            row.PendingRateCount = 0;
        }
    }
}



