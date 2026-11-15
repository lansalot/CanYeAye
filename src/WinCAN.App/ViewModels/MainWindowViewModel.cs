using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCAN.App.Services;
using WinCAN.Connections;
using WinCAN.Connections.Transports;
using WinCAN.Core;
using WinCAN.Core.Dbc;
using WinCAN.Core.FileFormats;
using WinCAN.Core.Logging;

namespace WinCAN.App.ViewModels;

public sealed record SignalRowViewModel(string Name, string Value, string Unit, string Description);

/// <summary>
/// Main window state: connection lifecycle, capture store, log pane, DBC decoding and frame
/// transmission. Frames arrive on transport threads and are batched onto the UI thread.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private const int MaxDisplayedFrames = 50_000;

    private readonly FrameStore _store = new();
    private readonly ConcurrentQueue<CanFrame> _pending = new();
    private readonly DispatcherTimer _flushTimer;
    private ICanConnection? _connection;
    private string? _selectedLogPath;

    public MainWindowViewModel()
    {
        ConnectionManager.FrameReceived += (_, frame) =>
        {
            if (CaptureSuspended)
            {
                return;
            }

            _store.Add(frame);
            _pending.Enqueue(frame);
        };

        ConnectionManager.ConnectionsChanged += (_, _) =>
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(BusSummary)));

        LogBus.EntryAdded += (_, entry) =>
            Dispatcher.UIThread.Post(() => AddLogLine(entry.ToString()));

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _flushTimer.Tick += (_, _) => FlushPendingFrames();
        _flushTimer.Start();

        AddLogLine("WinCAN ready.");
    }

    public CanConnectionManager ConnectionManager { get; } = new();

    public DbcDatabase Dbc { get; } = new();

    public LogBus LogBus { get; } = new();

    public IFileDialogService? FileDialogs { get; set; }

    public ObservableCollection<FrameRowViewModel> Frames { get; } = [];

    public ObservableCollection<SignalRowViewModel> Signals { get; } = [];

    public ObservableCollection<string> LogLines { get; } = [];

    public ObservableCollection<string> DbcFiles { get; } = [];

    public IReadOnlyList<string> ConnectionTypes { get; } =
    [
        "GVRET (serial)",
        "GVRET (TCP)",
        "Lawicel (serial)",
        "socketcand",
        "CANserver",
        "CANlogserver",
        "MQTT",
    ];

    [ObservableProperty]
    private string _selectedConnectionType = "GVRET (serial)";

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _connectionDetail = "No connection";

    [ObservableProperty]
    private bool _captureSuspended;

    [ObservableProperty]
    private FrameRowViewModel? _selectedFrame;

    [ObservableProperty]
    private string _serialPort = "COM3";

    [ObservableProperty]
    private int _baudRate = 115200;

    [ObservableProperty]
    private string _host = "127.0.0.1";

    [ObservableProperty]
    private int _port = 29536;

    [ObservableProperty]
    private uint _bitrate = 500_000;

    [ObservableProperty]
    private string _busNames = "can0";

    [ObservableProperty]
    private string _baseTopic = "wincan";

    [ObservableProperty]
    private string _sendBus = "0";

    [ObservableProperty]
    private string _sendId = "123";

    [ObservableProperty]
    private string _sendData = "01 02 03 04";

    [ObservableProperty]
    private bool _sendExtended;

    [ObservableProperty]
    private bool _sendFd;

    public bool IsConnected => _connection is not null;

    public string BusSummary
    {
        get
        {
            var connections = ConnectionManager.Connections;
            if (connections.Count == 0)
            {
                return "No buses";
            }

            return string.Join(
                ", ",
                connections.Select(c => $"{c.Connection.Name} (bus {c.FirstBus}..{c.LastBus})"));
        }
    }

    public string FrameCountText => $"{Frames.Count} frames";

    partial void OnSelectedFrameChanged(FrameRowViewModel? value) => UpdateSignals(value);

    [RelayCommand]
    private async Task OpenLogAsync()
    {
        if (FileDialogs is null)
        {
            return;
        }

        var path = await FileDialogs.PickOpenLogAsync();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        StatusText = $"Loading {Path.GetFileName(path)}...";
        try
        {
            var loaded = await Task.Run(() =>
            {
                var frames = new List<CanFrame>();
                var ok = LogFormatRegistry.AutoDetectLoad(path, frames);
                return (ok, frames);
            });

            if (!loaded.ok)
            {
                await FileDialogs.ShowMessageAsync("Load failed", "Could not autodetect the log format.");
                StatusText = "Load failed";
                return;
            }

            _store.Clear();
            Frames.Clear();
            _store.AddRange(loaded.frames);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var frame in loaded.frames)
                {
                    AppendFrame(frame);
                }
            });

            _selectedLogPath = path;
            StatusText = $"Loaded {loaded.frames.Count} frames from {Path.GetFileName(path)}";
            LogBus.Info(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = "Load failed";
            LogBus.Error(ex.Message);
            await FileDialogs.ShowMessageAsync("Load failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task SaveLogAsync()
    {
        if (FileDialogs is null || Frames.Count == 0)
        {
            return;
        }

        var path = await FileDialogs.PickSaveLogAsync(_selectedLogPath ?? "capture.csv");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            var frames = _store.Snapshot();
            var ok = await Task.Run(() => LogFormatRegistry.Save(path, 0, frames));
            StatusText = ok ? $"Saved {frames.Count} frames to {Path.GetFileName(path)}" : "Save failed";
            if (ok)
            {
                LogBus.Info(StatusText);
            }
            else
            {
                LogBus.Error(StatusText);
            }
        }
        catch (Exception ex)
        {
            LogBus.Error(ex.Message);
            await FileDialogs.ShowMessageAsync("Save failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task LoadDbcAsync()
    {
        if (FileDialogs is null)
        {
            return;
        }

        var path = await FileDialogs.PickOpenDbcAsync();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            var file = Dbc.Load(path);
            DbcFiles.Add(file.FileName);
            StatusText = $"Loaded DBC {file.FileName} ({file.Messages.Count} messages)";
            LogBus.Info(StatusText);
            UpdateSignals(SelectedFrame);
        }
        catch (Exception ex)
        {
            LogBus.Error(ex.Message);
            await FileDialogs.ShowMessageAsync("DBC load failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (_connection is not null)
        {
            return;
        }

        try
        {
            var connection = CreateConnection();
            connection.StateChanged += OnConnectionStateChanged;
            await ConnectionManager.ConnectAsync(connection);
            _connection = connection;
            ConnectionDetail = $"{connection.Name}: {connection.State}";
            StatusText = ConnectionDetail;
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(BusSummary));
        }
        catch (Exception ex)
        {
            LogBus.Error(ex.Message);
            StatusText = "Connection failed";
            if (FileDialogs is not null)
            {
                await FileDialogs.ShowMessageAsync("Connection failed", ex.Message);
            }
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        try
        {
            await ConnectionManager.DisconnectAsync(connection);
            await connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            LogBus.Warning(ex.Message);
        }

        connection.StateChanged -= OnConnectionStateChanged;
        _connection = null;
        ConnectionDetail = "No connection";
        StatusText = "Disconnected";
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(BusSummary));
    }

    [RelayCommand]
    private void ClearFrames()
    {
        _store.Clear();
        Frames.Clear();
        Signals.Clear();
        StatusText = "Frames cleared";
        OnPropertyChanged(nameof(FrameCountText));
    }

    [RelayCommand]
    private async Task SendFrameAsync()
    {
        if (_connection is null)
        {
            await ShowMessageAsync("Send frame", "Connect to a bus first.");
            return;
        }

        try
        {
            var id = CanIdParser.ParseId(SendId);
            var bus = (int)CanIdParser.Parse(SendBus);
            var data = ParseHexBytes(SendData);
            var frame = new CanFrame();
            frame.SetFrameId(id);
            frame.SetExtended(SendExtended || id > 0x7FF);
            frame.SetFrameType(SendFd ? FrameType.CanFdDataFrame : FrameType.CanDataFrame);
            if (SendFd)
            {
                frame.SetFlexibleDataRate(true);
            }

            frame.Bus = (byte)Math.Clamp(bus, 0, 255);
            frame.SetPayload(data);
            frame.SetReceived(false);
            await ConnectionManager.SendFrameAsync(frame);
            LogBus.Info($"TX bus {bus} id {CanIdParser.FormatId(id, frame.IsExtended)} [{data.Length}] {Convert.ToHexString(data)}");
        }
        catch (Exception ex)
        {
            LogBus.Error(ex.Message);
            await ShowMessageAsync("Send failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task GraphAsync()
    {
        if (SelectedFrame is null || FileDialogs is null)
        {
            return;
        }

        var message = Dbc.FindMessage(SelectedFrame.Frame);
        if (message is null || message.Signals.Count == 0)
        {
            await ShowMessageAsync("Graph", "Load a DBC that defines the selected frame (with signals) first.");
            return;
        }

        var signal = message.Signals.Values.First();
        var points = new List<(double X, double Y)>();
        var start = 0L;
        var first = true;
        foreach (var frame in _store.Snapshot())
        {
            if (frame.FrameId != message.Id)
            {
                continue;
            }

            if (first)
            {
                start = frame.Timestamp.ToMicroseconds();
                first = false;
            }

            if (signal.TryProcessAsDouble(frame, out var value))
            {
                points.Add(((frame.Timestamp.ToMicroseconds() - start) / 1_000_000.0, value));
            }
        }

        if (points.Count == 0)
        {
            await ShowMessageAsync("Graph", "No frames for this message were captured.");
            return;
        }

        var title = $"{message.Name} / {signal.Name}";
        new Views.GraphWindow(title, points).Show();
    }

    public void AddLogLine(string text)
    {
        LogLines.Add(text);
        if (LogLines.Count > 5000)
        {
            LogLines.RemoveAt(0);
        }
    }

    private ICanConnection CreateConnection()
    {
        return SelectedConnectionType switch
        {
            "GVRET (serial)" => new GvretConnection(new GvretConnectionOptions
            {
                SerialPort = SerialPort,
                BaudRate = BaudRate,
                Bus0Speed = Bitrate,
                Bus1Speed = Bitrate,
            }),
            "GVRET (TCP)" => new GvretConnection(new GvretConnectionOptions
            {
                Host = Host,
                TcpPort = Port,
                Bus0Speed = Bitrate,
                Bus1Speed = Bitrate,
                EspSerialMode = false,
            }),
            "Lawicel (serial)" => new LawicelConnection(new LawicelConnectionOptions
            {
                PortName = SerialPort,
                BaudRate = BaudRate,
                Bitrate = Bitrate,
            }),
            "socketcand" => new SocketCanDConnection(new SocketCanDConnectionOptions
            {
                Host = Host,
                Port = Port,
                BusNames = BusNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            }),
            "CANserver" => new CanServerConnection(new CanServerConnectionOptions(Host)),
            "CANlogserver" => new CanLogServerConnection(new CanLogServerConnectionOptions(Host, Port)),
            "MQTT" => new MqttConnection(new MqttConnectionOptions
            {
                Host = Host,
                Port = Port,
                BaseTopic = BaseTopic,
            }),
            _ => throw new InvalidOperationException($"Unknown connection type {SelectedConnectionType}."),
        };
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            ConnectionDetail = $"{_connection?.Name ?? "Connection"}: {e.State}";
            if (!string.IsNullOrEmpty(e.Message))
            {
                ConnectionDetail += $" ({e.Message})";
            }

            StatusText = ConnectionDetail;
        });

    private void FlushPendingFrames()
    {
        var added = false;
        while (_pending.TryDequeue(out var frame))
        {
            AppendFrame(frame);
            added = true;
        }

        if (added)
        {
            OnPropertyChanged(nameof(FrameCountText));
        }
    }

    private void AppendFrame(CanFrame frame)
    {
        Frames.Add(new FrameRowViewModel(frame));
        if (Frames.Count > MaxDisplayedFrames)
        {
            Frames.RemoveAt(0);
        }
    }

    private void UpdateSignals(FrameRowViewModel? row)
    {
        Signals.Clear();
        if (row is null)
        {
            return;
        }

        var message = Dbc.FindMessage(row.Frame);
        if (message is null)
        {
            return;
        }

        foreach (var signal in message.Signals.Values)
        {
            var text = signal.ProcessAsText(row.Frame, outputName: false, outputUnit: false).Trim();
            var description = signal.TryProcessAsInt(row.Frame, out var intValue) &&
                              signal.TryGetValueDescription(intValue, out var valueDescription)
                ? valueDescription
                : string.Empty;
            Signals.Add(new SignalRowViewModel(signal.Name, text, signal.UnitName, description));
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        if (FileDialogs is not null)
        {
            await FileDialogs.ShowMessageAsync(title, message);
        }
    }

    private static byte[] ParseHexBytes(string text)
    {
        var cleaned = text.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(",", " ")
            .Replace("-", " ");
        var tokens = cleaned.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 0)
            .ToArray();
        if (tokens.Length > 1)
        {
            var bytes = new byte[tokens.Length];
            for (var i = 0; i < tokens.Length; i++)
            {
                bytes[i] = byte.TryParse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)
                    ? b
                    : (byte)0;
            }

            return bytes;
        }

        var compact = new string(cleaned.Where(Uri.IsHexDigit).ToArray());
        if (compact.Length % 2 != 0)
        {
            compact = compact.PadLeft(compact.Length + 1, '0');
        }

        var result = new byte[compact.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = byte.Parse(compact.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return result;
    }
}
