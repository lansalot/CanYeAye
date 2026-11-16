using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanYeAye.App.Services;
using CanYeAye.Connections;
using CanYeAye.Core;
using CanYeAye.Core.Protocols;

namespace CanYeAye.App.ViewModels;

public partial class Obd2PidRow : ObservableObject
{
    public Obd2PidRow(string ecu, string pid, string name, string unit)
    {
        Ecu = ecu;
        Pid = pid;
        Name = name;
        Unit = unit;
    }

    public string Ecu { get; }

    public string Pid { get; }

    public string Name { get; }

    public string Unit { get; }

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private string _state = string.Empty;
}

/// <summary>OBD-II poller window: discovery, live PID values and the generated OBD DBC.</summary>
public partial class Obd2WindowViewModel : ObservableObject, IDisposable
{
    private readonly CanConnectionManager _manager;
    private readonly IFileDialogService _dialogs;
    private readonly IsoTpHandler _isoTp = new();
    private readonly Obd2Poller _poller;
    private readonly DispatcherTimer _serviceTimer;
    private readonly Dictionary<(uint Ecu, byte Pid, string Field), Obd2PidRow> _rows = [];

    public Obd2WindowViewModel(CanConnectionManager manager, IFileDialogService dialogs)
    {
        _manager = manager;
        _dialogs = dialogs;

        _isoTp.FrameToSend += (_, frame) => SendFrame(frame);
        _manager.FrameReceived += OnFrameReceived;

        _poller = new Obd2Poller(_isoTp);
        _poller.EcuFound += (_, id) => Dispatcher.UIThread.Post(() => Status = $"ECU found: 0x{id:X}");
        _poller.DiscoveryFinished += (_, _) => Dispatcher.UIThread.Post(OnDiscoveryFinished);
        _poller.PidUpdated += (_, update) => Dispatcher.UIThread.Post(() => ApplyPid(update.EcuReplyId, update.Pid, update.Values));
        _poller.NegativeReply += (_, n) => Dispatcher.UIThread.Post(() => Status = $"Negative reply from 0x{n.EcuReplyId:X}: service 0x{n.Service:X2} code 0x{n.Code:X2}");
        _poller.RequestTimedOut += (_, t) => Dispatcher.UIThread.Post(() => Status = $"Timeout waiting for 0x{t.EcuReplyId:X}");
        _poller.VehicleInfoRead += (_, v) => Dispatcher.UIThread.Post(() =>
        {
            VehicleInfo.Add($"{v.Pid:X2} from 0x{v.EcuReplyId:X}: {v.Text}");
            Status = $"Vehicle info: {v.Text}";
        });
        _poller.StatsChanged += (_, _) => Dispatcher.UIThread.Post(UpdateStats);

        _serviceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2) };
        _serviceTimer.Tick += (_, _) =>
        {
            if (_poller.NeedsService)
            {
                _poller.Service();
            }
        };
        _serviceTimer.Start();
    }

    public ObservableCollection<Obd2PidRow> Pids { get; } = [];

    public ObservableCollection<string> VehicleInfo { get; } = [];

    public IReadOnlyList<string> AddressingModes { get; } = ["11-bit", "29-bit"];

    [ObservableProperty]
    private string _selectedAddressing = "11-bit";

    [ObservableProperty]
    private int _bus;

    [ObservableProperty]
    private string _status = "Idle";

    [ObservableProperty]
    private string _stats = string.Empty;

    [ObservableProperty]
    private bool _polling;

    partial void OnBusChanged(int value) => _poller.SetBus(value);

    partial void OnSelectedAddressingChanged(string value)
    {
        _poller.SetAddressing(value == "29-bit"
            ? Obd2Poller.Addressing.Extended29Bit
            : Obd2Poller.Addressing.Standard11Bit);
    }

    [RelayCommand]
    private void Discover()
    {
        Pids.Clear();
        _rows.Clear();
        VehicleInfo.Clear();
        _poller.SetBus(Bus);
        _poller.Discover();
        Status = "Discovering ECUs...";
    }

    [RelayCommand]
    private void TogglePolling()
    {
        if (Polling)
        {
            _poller.Stop();
            Polling = false;
            Status = "Polling stopped";
        }
        else
        {
            _poller.Start();
            Polling = true;
            Status = "Polling";
        }
    }

    [RelayCommand]
    private void ReadVin()
    {
        var ecu = _poller.Ecus.FirstOrDefault();
        if (ecu is null)
        {
            Status = "Discover an ECU first";
            return;
        }

        if (_poller.ReadVin(ecu.ReplyId))
        {
            Status = $"Reading VIN from 0x{ecu.ReplyId:X}...";
        }
    }

    [RelayCommand]
    private async Task SaveDbcAsync()
    {
        var ids = _poller.Ecus.Select(e => e.ReplyId).ToList();
        if (ids.Count == 0)
        {
            ids = [0x7E8];
        }

        var path = await _dialogs.PickSaveLogAsync("obd2.dbc");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, Obd2Dbc.MakeDbc(ids));
            Status = $"Saved OBD-II DBC to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Save DBC failed", ex.Message);
        }
    }

    public void Dispose()
    {
        _serviceTimer.Stop();
        _manager.FrameReceived -= OnFrameReceived;
        _poller.Stop();
    }

    private void OnDiscoveryFinished()
    {
        var items = new List<Obd2Poller.PollItem>();
        foreach (var ecu in _poller.Ecus)
        {
            foreach (var pid in ecu.SupportedPids)
            {
                items.Add(new Obd2Poller.PollItem(ecu.ReplyId, pid, 500));
            }
        }

        _poller.SetPollList(items);
        Status = $"Discovery finished: {_poller.Ecus.Count} ECU(s), {items.Count} PID(s)";

        if (items.Count > 0 && !Polling)
        {
            _poller.Start();
            Polling = true;
        }
    }

    private void OnFrameReceived(object? sender, CanFrame frame) =>
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _isoTp.ProcessFrame(frame);
            }
            catch (Exception ex)
            {
                Status = ex.Message;
            }
        });

    private async void SendFrame(CanFrame frame)
    {
        try
        {
            await _manager.SendFrameAsync(frame);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    private void ApplyPid(uint ecuReplyId, byte pid, IReadOnlyList<Obd2.Value> values)
    {
        var info = Obd2.FindMode01(pid);
        var pidName = info?.Name ?? $"PID {pid:X2}";
        foreach (var value in values)
        {
            var key = (ecuReplyId, pid, value.Field.Name);
            if (!_rows.TryGetValue(key, out var row))
            {
                row = new Obd2PidRow(
                    $"0x{ecuReplyId:X}",
                    $"{pid:X2}",
                    $"{pidName} / {value.Field.Name}",
                    value.Field.Unit);
                _rows[key] = row;
                Pids.Add(row);
            }

            row.Value = value.Text.Length > 0
                ? value.Text
                : value.Scaled.ToString("g6", CultureInfo.InvariantCulture);
        }
    }

    private void UpdateStats()
    {
        var stats = _poller.Statistics;
        Stats = $"Requests {stats.Requests}, replies {stats.Replies} ({stats.RepliesLastSecond}/s), " +
                $"timeouts {stats.Timeouts}, negative {stats.NegativeReplies}";
    }
}
