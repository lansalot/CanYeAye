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
using WinCAN.Core.Analysis;
using WinCAN.Core.Dbc;
using WinCAN.Core.FileFormats;
using WinCAN.Core.Formatting;
using WinCAN.Core.Logging;
using WinCAN.Core.Protocols;

namespace WinCAN.App.ViewModels;

/// <summary>
/// Main window state: connection lifecycle, capture store, frame filtering/overwrite/interpret
/// (the right-hand frame panel), log pane, DBC decoding and frame transmission.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private const int MaxDisplayedFrames = 50_000;

    private readonly FrameStore _store = new();
    private readonly ConcurrentQueue<CanFrame> _pending = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _nodeTextTimer;
    private readonly Dictionary<uint, bool> _idFilters = [];
    private readonly Dictionary<byte, bool> _busFilters = [];
    private readonly Dictionary<uint, FrameFilterItem> _frameFilterItems = [];
    private readonly List<FrameFilterItem> _allFrameFilters = [];
    private readonly List<BusFilterItem> _allBusFilters = [];
    private readonly Dictionary<(byte Bus, uint Id), FrameRowViewModel> _overwriteRows = [];
    private ICanConnection? _connection;
    private string? _selectedLogPath;
    private long _framesThisSecond;
    private bool _suppressFilterRebuild;

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
            Interlocked.Increment(ref _framesThisSecond);
        };

        ConnectionManager.ConnectionsChanged += (_, _) =>
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(BusSummary)));

        NodeNames.Changed += (_, _) => SyncNodeNameItems();

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _flushTimer.Tick += (_, _) => FlushPendingFrames();
        _flushTimer.Start();

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) =>
        {
            FramesPerSecond = Interlocked.Exchange(ref _framesThisSecond, 0);
            TotalFramesCaptured = _store.Count;
        };
        _statsTimer.Start();

        _nodeTextTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _nodeTextTimer.Tick += (_, _) =>
        {
            _nodeTextTimer.Stop();
            foreach (var row in Frames)
            {
                row.RefreshNodes();
            }
        };
    }

    public CanConnectionManager ConnectionManager { get; } = new();

    public DbcDatabase Dbc { get; } = new();

    public LogBus LogBus { get; } = new();

    public IFileDialogService? FileDialogs { get; set; }

    public ObservableCollection<FrameRowViewModel> Frames { get; } = [];

    public ObservableCollection<FrameFilterItem> FrameFilters { get; } = [];

    public ObservableCollection<BusFilterItem> BusFilters { get; } = [];

    /// <summary>User-assigned J1939 source/node names (also shown in the filter list and graph).</summary>
    public J1939NodeNames NodeNames { get; } = new();

    /// <summary>Editable node-name rows for the filter window.</summary>
    public ObservableCollection<NodeNameItem> NodeNameItems { get; } = [];

    private readonly Dictionary<int, NodeNameItem> _nodeNameItems = [];
    private NodeNameItem? _broadcastNode;
    private bool _suppressNodeSelection;

    /// <summary>Snapshot of the capture (loaded log or live frames) for analysis windows.</summary>
    public IReadOnlyList<CanFrame> FrameSnapshot() => _store.Snapshot();

    /// <summary>Saved node positions for the J1939 node graph.</summary>
    public List<WinCAN.Core.Settings.NodeGraphPosition> NodeGraphLayout { get; set; } = [];

    public double NodeGraphZoom { get; set; } = 1.0;

    public double NodeGraphPanX { get; set; }

    public double NodeGraphPanY { get; set; }

    public double NodeGraphWindowWidth { get; set; } = 1050;

    public double NodeGraphWindowHeight { get; set; } = 720;

    public int NodeGraphWindowX { get; set; } = int.MinValue;

    public int NodeGraphWindowY { get; set; } = int.MinValue;

    /// <summary>Raised when the view should scroll to the newest frame (auto-scroll).</summary>
    public event EventHandler? ScrollToEndRequested;

    /// <summary>Raised by Expand/Collapse All to change grid row sizing.</summary>
    public event EventHandler<bool>? RowExpansionChanged;

    public IReadOnlyList<string> ConnectionTypes { get; } =
    [
        "Serial Connection (GVRET)",
        "Network connection (GVRET)",
        "LAWICEL / SLCAN Serial",
        "PEAK PCAN-USB",
        "socketcand (SocketCAN over Ethernet)",
        "MQTT Connection",
        "CANserver",
        "CANlogserver (TCP ASCII)",
    ];

    public IReadOnlyList<string> SaveFormatNames { get; } =
        LogFormatRegistry.SaveFilters.Select(f => f.Name).ToList();

    [ObservableProperty]
    private string _selectedSaveFormat = LogFormatRegistry.SaveFilters[0].Name;

    [ObservableProperty]
    private string _selectedConnectionType = "Serial Connection (GVRET)";

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _connectionDetail = "No connection";

    [ObservableProperty]
    private bool _captureSuspended;

    [ObservableProperty]
    private FrameRowViewModel? _selectedFrame;

    [ObservableProperty]
    private long _totalFramesCaptured;

    [ObservableProperty]
    private long _framesPerSecond;

    [ObservableProperty]
    private bool _keepFiltersWhenClearing;

    [ObservableProperty]
    private bool _autoScrollWindow = true;

    [ObservableProperty]
    private bool _overwriteMode;

    [ObservableProperty]
    private bool _interpretFrames;

    [ObservableProperty]
    private string _filterSearch = string.Empty;

    [ObservableProperty]
    private string _filterSummary = "0 / 0 IDs shown";

    [ObservableProperty]
    private int _uiScalePercent = 100;

    partial void OnUiScalePercentChanged(int value) => OnPropertyChanged(nameof(UiScaleLabel));

    public string UiScaleLabel => $"{UiScalePercent}%";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isLoadIndeterminate;

    [ObservableProperty]
    private double _loadProgress;

    [ObservableProperty]
    private string _loadStatus = string.Empty;

    [ObservableProperty]
    private string _serialPort = "COM3";

    [ObservableProperty]
    private int _baudRate = 115200;

    [ObservableProperty]
    private string _host = "127.0.0.1";

    [ObservableProperty]
    private int _gvretTcpPort = 23;

    [ObservableProperty]
    private int _socketCandPort = 29536;

    [ObservableProperty]
    private int _mqttPort = 1883;

    [ObservableProperty]
    private int _canLogServerPort = 23;

    /// <summary>PCAN-USB channels reported by the PCAN-Basic driver (1-based).</summary>
    public ObservableCollection<int> PcanChannels { get; } = [];

    [ObservableProperty]
    private int _selectedPcanChannel = 1;

    [ObservableProperty]
    private string _pcanStatus = "Use Detect to look for a PCAN-USB adapter.";

    [RelayCommand]
    private void DetectPcan()
    {
        var channels = PcanConnection.GetAvailableChannels();
        PcanChannels.Clear();
        foreach (var channel in channels)
        {
            PcanChannels.Add(channel);
        }

        if (channels.Count == 0)
        {
            for (var channel = 1; channel <= 16; channel++)
            {
                PcanChannels.Add(channel);
            }

            PcanStatus = "No PCAN-USB adapter found (check the PEAK driver and the USB connection).";
            return;
        }

        if (!channels.Contains(SelectedPcanChannel))
        {
            SelectedPcanChannel = channels[0];
        }

        var hardware = PcanConnection.GetHardwareName(SelectedPcanChannel);
        PcanStatus = string.IsNullOrEmpty(hardware)
            ? $"PCAN channel(s) available: {string.Join(", ", channels.Select(channel => $"USB{channel}"))}"
            : $"{hardware} on USB{SelectedPcanChannel}";
    }

    [ObservableProperty]
    private uint _bitrate = 500_000;

    /// <summary>Valid CAN bus speeds offered by the connection dialog (re/connectionwindow.cpp).</summary>
    public ObservableCollection<uint> BusSpeeds { get; } =
    [
        33_333,
        50_000,
        83_333,
        100_000,
        125_000,
        250_000,
        500_000,
        1_000_000,
    ];

    /// <summary>Adds a saved speed that is not in the standard list (SavvyCAN does the same).</summary>
    public void EnsureBusSpeed(uint speed)
    {
        if (speed == 0 || BusSpeeds.Contains(speed))
        {
            return;
        }

        var index = 0;
        while (index < BusSpeeds.Count && BusSpeeds[index] < speed)
        {
            index++;
        }

        BusSpeeds.Insert(index, speed);
    }

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

    public bool IsGvretSerial => SelectedConnectionType == "Serial Connection (GVRET)";

    public bool IsGvretTcp => SelectedConnectionType == "Network connection (GVRET)";

    public bool IsLawicel => SelectedConnectionType == "LAWICEL / SLCAN Serial";

    public bool IsPcan => SelectedConnectionType == "PEAK PCAN-USB";

    public bool IsSocketCand => SelectedConnectionType == "socketcand (SocketCAN over Ethernet)";

    public bool IsMqtt => SelectedConnectionType == "MQTT Connection";

    public bool IsCanServer => SelectedConnectionType == "CANserver";

    public bool IsCanLogServer => SelectedConnectionType == "CANlogserver (TCP ASCII)";

    public bool CanConnect => true;

    partial void OnSelectedConnectionTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsGvretSerial));
        OnPropertyChanged(nameof(IsGvretTcp));
        OnPropertyChanged(nameof(IsLawicel));
        OnPropertyChanged(nameof(IsPcan));
        OnPropertyChanged(nameof(IsSocketCand));
        OnPropertyChanged(nameof(IsMqtt));
        OnPropertyChanged(nameof(IsCanServer));
        OnPropertyChanged(nameof(IsCanLogServer));
        OnPropertyChanged(nameof(CanConnect));
        if (IsPcan)
        {
            DetectPcan();
        }
    }

    public string CaptureButtonText => CaptureSuspended ? "Restart Capturing" : "Suspend Capturing";

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

    partial void OnCaptureSuspendedChanged(bool value) => OnPropertyChanged(nameof(CaptureButtonText));

    partial void OnOverwriteModeChanged(bool value) => RebuildDisplay();

    partial void OnInterpretFramesChanged(bool value)
    {
        RefreshFilterLabels();
        RebuildDisplay();
    }

    partial void OnFilterSearchChanged(string value)
    {
        ApplySearchToFilterList();
        RebuildDisplay();
    }

    [RelayCommand]
    private void ToggleCapture() => CaptureSuspended = !CaptureSuspended;

    [RelayCommand]
    private void NormalizeTiming()
    {
        var frames = _store.Snapshot();
        if (frames.Count == 0)
        {
            return;
        }

        var minimum = frames.Min(f => f.Timestamp.ToMicroseconds());
        foreach (var frame in frames)
        {
            frame.Timestamp = FrameTimestamp.FromMicroseconds(frame.Timestamp.ToMicroseconds() - minimum);
        }

        RebuildDisplay();
        StatusText = "Frame timing normalized";
    }

    [RelayCommand]
    private void FilterAll() => SetAllFilters(true);

    [RelayCommand]
    private void FilterNone() => SetAllFilters(false);

    [RelayCommand]
    private void ExpandAll() => RowExpansionChanged?.Invoke(this, true);

    [RelayCommand]
    private void CollapseAll() => RowExpansionChanged?.Invoke(this, false);

    [RelayCommand]
    private async Task OpenLogAsync()
    {
        if (FileDialogs is null || IsLoading)
        {
            return;
        }

        var path = await FileDialogs.PickOpenLogAsync();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        await LoadLogFileAsync(path);
    }

    /// <summary>Loads a log file (also used for a path passed on the command line).</summary>
    public async Task LoadLogFileAsync(string path)
    {
        if (IsLoading || !File.Exists(path))
        {
            return;
        }

        IsLoading = true;
        IsLoadIndeterminate = true;
        LoadProgress = 0;
        LoadStatus = $"Reading {Path.GetFileName(path)}...";
        StatusText = LoadStatus;
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
                await ShowMessageAsync("Load failed", "Could not autodetect the log format.");
                StatusText = "Load failed";
                return;
            }

            _store.Clear();
            _pending.Clear();
            Frames.Clear();
            _overwriteRows.Clear();

            // A new log starts from a clean filter set: the old ID/bus filters would
            // otherwise hide the freshly loaded frames.
            ClearFilterState();

            _store.AddRange(loaded.frames);

            await PopulateLoadedAsync(loaded.frames);

            _selectedLogPath = path;
            TotalFramesCaptured = _store.Count;
            StatusText = loaded.frames.Count > MaxDisplayedFrames
                ? $"Loaded {loaded.frames.Count:N0} frames from {Path.GetFileName(path)} (showing the last {MaxDisplayedFrames:N0})"
                : $"Loaded {loaded.frames.Count:N0} frames from {Path.GetFileName(path)}";
            LogBus.Info(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = "Load failed";
            LogBus.Error(ex.Message);
            await ShowMessageAsync("Load failed", ex.Message);
        }
        finally
        {
            IsLoading = false;
            IsLoadIndeterminate = false;
        }
    }

    [RelayCommand]
    private async Task SaveNodeNamesAsync()
    {
        if (FileDialogs is null)
        {
            return;
        }

        var path = await FileDialogs.PickSaveNodeNamesAsync("j1939-nodes.txt");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            NodeNames.Save(path);
            StatusText = $"Saved {NodeNames.Count} node names to {Path.GetFileName(path)}";
            LogBus.Info(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = "Save failed";
            LogBus.Error(ex.Message);
            await ShowMessageAsync("Save failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task LoadNodeNamesAsync()
    {
        if (FileDialogs is null)
        {
            return;
        }

        var path = await FileDialogs.PickOpenNodeNamesAsync();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        if (!NodeNames.Load(path))
        {
            await ShowMessageAsync("Load failed", "Could not read the node names file.");
            return;
        }

        SyncNodeNameItems();
        foreach (var item in NodeNameItems)
        {
            item.Name = NodeNames[item.Address] ?? string.Empty;
        }

        RefreshFilterLabels();
        StatusText = $"Loaded {NodeNames.Count} node names from {Path.GetFileName(path)}";
        LogBus.Info(StatusText);
    }

    [RelayCommand]
    private async Task SaveLogAsync()    {
        if (FileDialogs is null || Frames.Count == 0)
        {
            return;
        }

        var formatIndex = Math.Max(0, SaveFormatNames.ToList().IndexOf(SelectedSaveFormat));
        var extension = LogFormatRegistry.SaveFilters[formatIndex].Format?.DefaultExtension ?? ".csv";
        var suggested = Path.GetFileNameWithoutExtension(_selectedLogPath ?? "capture") + extension;
        var path = await FileDialogs.PickSaveLogAsync(suggested);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            var frames = _store.Snapshot();
            var ok = await Task.Run(() => LogFormatRegistry.Save(path, formatIndex, frames));
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
        _pending.Clear();
        Frames.Clear();
        _overwriteRows.Clear();
        TotalFramesCaptured = 0;
        FramesPerSecond = 0;
        Interlocked.Exchange(ref _framesThisSecond, 0);

        if (!KeepFiltersWhenClearing)
        {
            ClearFilterState();
        }
        else
        {
            UpdateFilterSummary();
        }

        StatusText = "Frames cleared";
    }

    /// <summary>Drops every ID/bus filter and rebuilds the empty filter lists.</summary>
    private void ClearFilterState()
    {
        _idFilters.Clear();
        _busFilters.Clear();
        _frameFilterItems.Clear();
        _allFrameFilters.Clear();
        _allBusFilters.Clear();
        FrameFilters.Clear();
        BusFilters.Clear();
        ResetNodeSelections();
        FilterSearch = string.Empty;
        UpdateFilterSummary();
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
            LogBus.Info($"TX bus {bus} id {CanIdParser.FormatId(id, frame.IsExtended)} [{data.Length}] {HexFormat.SpaceDelimited(data)}");
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

    /// <summary>Recomputes interpretations after the DBC list changes.</summary>
    public void OnDbcChanged()
    {
        RefreshFilterLabels();
        RebuildDisplay();
    }

    /// <summary>Loads a DBC file (used by the DBC manager and command-line startup).</summary>
    public void LoadDbcFile(string path)
    {
        try
        {
            var file = Dbc.Load(path);
            LogBus.Info($"Loaded DBC {file.FileName} ({file.Messages.Count} messages)");
            OnDbcChanged();
        }
        catch (Exception ex)
        {
            LogBus.Error($"{path}: {ex.Message}");
        }
    }

    private async Task PopulateLoadedAsync(IReadOnlyList<CanFrame> frames)
    {
        Frames.Clear();
        _overwriteRows.Clear();
        IsLoadIndeterminate = false;
        LoadProgress = 0;
        LoadStatus = $"Preparing {frames.Count:N0} frames...";

        if (OverwriteMode)
        {
            BuildOverwriteRows(frames);
            LoadProgress = 100;
            await Task.Yield();
            return;
        }

        var matching = new List<CanFrame>(Math.Min(frames.Count, MaxDisplayedFrames));
        var lastReport = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            RegisterFilterEntries(frame);
            if (PassesFilters(frame))
            {
                matching.Add(frame);
            }

            if (i - lastReport >= 2000 || i == frames.Count - 1)
            {
                lastReport = i;
                var fraction = frames.Count == 0 ? 1.0 : (i + 1) / (double)frames.Count;
                LoadProgress = fraction * 50.0;
                LoadStatus = $"Scanning frames {i + 1:N0} / {frames.Count:N0}";
                await Task.Yield();
            }
        }

        var start = Math.Max(0, matching.Count - MaxDisplayedFrames);
        var toAdd = matching.Count - start;
        for (var i = start; i < matching.Count; i++)
        {
            Frames.Add(CreateRow(matching[i]));
            if ((i - start) % 5000 == 0 || i == matching.Count - 1)
            {
                LoadProgress = 50.0 + (toAdd == 0 ? 50.0 : (i - start + 1) * 50.0 / toAdd);
                LoadStatus = $"Adding frames {i - start + 1:N0} / {toAdd:N0}";
                await Task.Yield();
            }
        }

        LoadProgress = 100;
    }

    private ICanConnection CreateConnection()
    {
        return SelectedConnectionType switch
        {
            "Serial Connection (GVRET)" => new GvretConnection(new GvretConnectionOptions
            {
                SerialPort = SerialPort,
                BaudRate = BaudRate,
                Bus0Speed = Bitrate,
                Bus1Speed = Bitrate,
            }),
            "Network connection (GVRET)" => new GvretConnection(new GvretConnectionOptions
            {
                Host = Host,
                TcpPort = GvretTcpPort,
                Bus0Speed = Bitrate,
                Bus1Speed = Bitrate,
                EspSerialMode = false,
            }),
            "LAWICEL / SLCAN Serial" => new LawicelConnection(new LawicelConnectionOptions
            {
                PortName = SerialPort,
                BaudRate = BaudRate,
                Bitrate = Bitrate,
            }),
            "PEAK PCAN-USB" => new PcanConnection(new PcanConnectionOptions
            {
                Channel = SelectedPcanChannel,
                Bitrate = Bitrate,
            }),
            "socketcand (SocketCAN over Ethernet)" => new SocketCanDConnection(new SocketCanDConnectionOptions
            {
                Host = Host,
                Port = SocketCandPort,
                BusNames = BusNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            }),
            "MQTT Connection" => new MqttConnection(new MqttConnectionOptions
            {
                Host = Host,
                Port = MqttPort,
                BaseTopic = BaseTopic,
            }),
            "CANserver" => new CanServerConnection(new CanServerConnectionOptions(Host)),
            "CANlogserver (TCP ASCII)" => new CanLogServerConnection(new CanLogServerConnectionOptions(Host, CanLogServerPort)),
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
            RegisterFilterEntries(frame);
            if (!PassesFilters(frame))
            {
                continue;
            }

            if (OverwriteMode)
            {
                UpdateOverwriteRow(frame);
            }
            else
            {
                Frames.Add(CreateRow(frame));
                if (Frames.Count > MaxDisplayedFrames)
                {
                    Frames.RemoveAt(0);
                }
            }

            added = true;
        }

        if (added)
        {
            TotalFramesCaptured = _store.Count;
            if (AutoScrollWindow)
            {
                ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void RebuildDisplay()
    {
        var snapshot = _store.Snapshot();
        Frames.Clear();
        _overwriteRows.Clear();

        if (OverwriteMode)
        {
            BuildOverwriteRows(snapshot);
        }
        else
        {
            var matching = new List<CanFrame>();
            foreach (var frame in snapshot)
            {
                RegisterFilterEntries(frame);
                if (PassesFilters(frame))
                {
                    matching.Add(frame);
                }
            }

            var start = Math.Max(0, matching.Count - MaxDisplayedFrames);
            for (var i = start; i < matching.Count; i++)
            {
                Frames.Add(CreateRow(matching[i]));
            }
        }

        TotalFramesCaptured = _store.Count;
    }

    private void BuildOverwriteRows(IReadOnlyList<CanFrame> frames)
    {
        var order = new List<(byte Bus, uint Id)>();
        var latest = new Dictionary<(byte Bus, uint Id), CanFrame>();
        var counts = new Dictionary<(byte Bus, uint Id), uint>();
        var deltas = new Dictionary<(byte Bus, uint Id), ulong>();

        foreach (var frame in frames)
        {
            RegisterFilterEntries(frame);
            if (!PassesFilters(frame))
            {
                continue;
            }

            var key = (frame.Bus, frame.FrameId);
            if (latest.TryGetValue(key, out var previous))
            {
                counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 2;
                deltas[key] = (ulong)Math.Max(0, frame.Timestamp.ToMicroseconds() - previous.Timestamp.ToMicroseconds());
                latest[key] = frame;
            }
            else
            {
                order.Add(key);
                latest[key] = frame;
                counts[key] = 1;
                deltas[key] = 0;
            }
        }

        foreach (var key in order)
        {
            var display = latest[key].Clone();
            display.FrameCount = counts[key];
            display.TimeDelta = deltas[key];
            var row = CreateRow(display);
            Frames.Add(row);
            _overwriteRows[key] = row;
        }
    }

    private void UpdateOverwriteRow(CanFrame frame)
    {
        var key = (frame.Bus, frame.FrameId);
        if (_overwriteRows.TryGetValue(key, out var row))
        {
            var previous = row.Frame;
            var display = frame.Clone();
            display.FrameCount = previous.FrameCount + 1;
            display.TimeDelta = (ulong)Math.Max(0, frame.Timestamp.ToMicroseconds() - previous.Timestamp.ToMicroseconds());
            row.SetFrame(display);
            if (InterpretFrames)
            {
                row.Interpretation = Describe(display);
            }
        }
        else
        {
            var display = frame.Clone();
            display.FrameCount = 1;
            display.TimeDelta = 0;
            row = CreateRow(display);
            Frames.Add(row);
            _overwriteRows[key] = row;
        }
    }

    private FrameRowViewModel CreateRow(CanFrame frame)
    {
        var row = new FrameRowViewModel(frame)
        {
            Interpretation = InterpretFrames ? Describe(frame) : string.Empty,
        };
        row.AttachNodeNames(NodeNames);
        return row;
    }

    private string Describe(CanFrame frame)
    {
        var message = Dbc.FindMessage(frame);
        if (message is null || message.Signals.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string> { $"<{message.Name}>" };
        foreach (var signal in message.ActiveSignals(frame))
        {
            var text = signal.ProcessAsText(frame).TrimEnd();
            if (text.Length > 0)
            {
                parts.Add(text);
            }
        }

        return string.Join(Environment.NewLine, parts);
    }

    private void RegisterFilterEntries(CanFrame frame)
    {
        RegisterNodeName(frame);

        if (!_idFilters.ContainsKey(frame.FrameId))
        {
            // A new ID of a node that the user has deselected starts hidden too.
            var visible = true;
            if (frame.IsExtended)
            {
                if (_nodeNameItems.TryGetValue((int)(frame.FrameId & 0xFF), out var node) &&
                    node.IsChecked == false)
                {
                    visible = false;
                }

                if (IsBroadcastId(frame.FrameId) && _broadcastNode is { IsChecked: false })
                {
                    visible = false;
                }
            }

            _idFilters[frame.FrameId] = visible;
            var item = new FrameFilterItem(
                frame.FrameId,
                frame.IsExtended,
                BuildFilterLabel(frame.FrameId, frame.IsExtended),
                OnIdFilterChanged,
                visible);
            _frameFilterItems[frame.FrameId] = item;
            _allFrameFilters.Add(item);
            if (MatchesSearch(item.Label))
            {
                InsertFrameFilterSorted(item);
            }

            UpdateFilterSummary();
            RefreshNodeSelectionStates();
        }

        if (!_busFilters.ContainsKey(frame.Bus))
        {
            _busFilters[frame.Bus] = true;
            var item = new BusFilterItem(frame.Bus, OnBusFilterChanged);
            _allBusFilters.Add(item);
            BusFilters.Add(item);
        }
    }

    private bool PassesFilters(CanFrame frame)
    {
        if (!_idFilters.TryGetValue(frame.FrameId, out var idVisible) || !idVisible)
        {
            return false;
        }

        if (!_busFilters.TryGetValue(frame.Bus, out var busVisible) || !busVisible)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(FilterSearch) &&
            (!_frameFilterItems.TryGetValue(frame.FrameId, out var item) ||
             !item.Label.Contains(FilterSearch, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    private void OnIdFilterChanged(FrameFilterItem item)
    {
        _idFilters[item.Id] = item.IsChecked;
        UpdateFilterSummary();
        RefreshNodeSelectionStates();
        if (!_suppressFilterRebuild)
        {
            RebuildDisplay();
        }
    }

    private void OnBusFilterChanged(BusFilterItem item)
    {
        _busFilters[item.Bus] = item.IsChecked;
        if (!_suppressFilterRebuild)
        {
            RebuildDisplay();
        }
    }

    private void SetAllFilters(bool value)
    {
        _suppressFilterRebuild = true;
        foreach (var item in _allFrameFilters)
        {
            item.IsChecked = value;
        }

        _suppressFilterRebuild = false;
        UpdateFilterSummary();
        RebuildDisplay();
    }

    private bool MatchesSearch(string label) =>
        string.IsNullOrEmpty(FilterSearch) ||
        label.Contains(FilterSearch, StringComparison.OrdinalIgnoreCase);

    private string BuildFilterLabel(uint id, bool extended)
    {
        var label = "0x" + CanIdParser.FormatId(id, extended);
        if (extended)
        {
            var sourceName = NodeNames[(int)(id & 0xFF)];
            if (!string.IsNullOrEmpty(sourceName))
            {
                label += " [" + sourceName + "]";
            }
        }

        if (InterpretFrames)
        {
            var message = Dbc.FindMessage(id);
            if (message is not null)
            {
                label += " " + message.Name;
            }
        }

        return label;
    }

    private void RegisterNodeName(CanFrame frame)
    {
        if (!frame.IsExtended)
        {
            return;
        }

        // Any J1939 traffic gets the pseudo-node that covers all broadcast destinations.
        EnsureBroadcastItem();
        var id = J1939Id.Decode(frame.FrameId);
        EnsureNodeItem(id.Source);
    }

    private void EnsureBroadcastItem()
    {
        if (_broadcastNode is not null)
        {
            return;
        }

        _broadcastNode = new NodeNameItem(
            NodeNameItem.BroadcastAddress,
            null,
            IgnoreNodeName,
            OnNodeSelectionChanged,
            isBroadcast: true);
        NodeNameItems.Insert(0, _broadcastNode);
    }

    private static void IgnoreNodeName(int address, string? name)
    {
        // The Broadcast row is not a nameable node.
    }

    private static bool IsBroadcastId(uint id)
    {
        var j1939 = J1939Id.Decode(id);
        return j1939.IsBroadcast || j1939.Destination == 0xFF;
    }

    private void EnsureNodeItem(int address)
    {
        if (!_nodeNameItems.ContainsKey(address))
        {
            InsertNodeNameItem(address);
        }
    }

    /// <summary>Creates/updates the editable rows for names restored from settings or a file.</summary>
    public void SyncNodeNameItems()
    {
        foreach (var address in NodeNames.Snapshot().Keys)
        {
            if (!_nodeNameItems.ContainsKey(address))
            {
                InsertNodeNameItem(address);
            }
        }

        foreach (var item in NodeNameItems)
        {
            if (item.IsBroadcast)
            {
                continue;
            }

            var name = NodeNames[item.Address] ?? string.Empty;
            if (!string.Equals(item.Name, name, StringComparison.Ordinal))
            {
                item.Name = name;
            }
        }

        if (_nodeTextTimer is not null)
        {
            _nodeTextTimer.Stop();
            _nodeTextTimer.Start();
        }
    }

    private void InsertNodeNameItem(int address)
    {
        var item = new NodeNameItem(
            address,
            NodeNames[address],
            OnNodeNameChanged,
            OnNodeSelectionChanged);
        _nodeNameItems[address] = item;
        var index = 0;
        while (index < NodeNameItems.Count && NodeNameItems[index].Address < address)
        {
            index++;
        }

        NodeNameItems.Insert(index, item);
    }

    [RelayCommand]
    private void ResetNodeNames()
    {
        if (NodeNames.Count == 0)
        {
            return;
        }

        NodeNames.Clear();
        SyncNodeNameItems();
        RefreshFilterLabels();
        StatusText = "Node names cleared";
    }

    private void OnNodeNameChanged(int address, string? name)
    {
        NodeNames.SetName(address, name);
        RefreshFilterLabels();
    }

    /// <summary>
    /// Selecting a node only checks/unchecks the ID filters of that source, so the ID tab
    /// remains the single source of truth for what is displayed.
    /// </summary>
    private void OnNodeSelectionChanged(NodeNameItem item)
    {
        if (_suppressNodeSelection)
        {
            return;
        }

        ApplyNodeSelection(item.Address, item.IsChecked == true);
        RebuildDisplay();
    }

    private void ApplyNodeSelection(int address, bool selected)
    {
        var broadcast = address == NodeNameItem.BroadcastAddress;
        _suppressFilterRebuild = true;
        try
        {
            foreach (var filter in _allFrameFilters)
            {
                var matches = filter.Extended &&
                    (broadcast
                        ? IsBroadcastId(filter.Id)
                        : (int)(filter.Id & 0xFF) == address);
                if (!matches || filter.IsChecked == selected)
                {
                    continue;
                }

                filter.IsChecked = selected;
            }
        }
        finally
        {
            _suppressFilterRebuild = false;
        }

        UpdateFilterSummary();
    }

    [RelayCommand]
    private void NodeFilterAll() => SetAllNodeSelections(true);

    [RelayCommand]
    private void NodeFilterNone() => SetAllNodeSelections(false);

    private void SetAllNodeSelections(bool selected)
    {
        _suppressNodeSelection = true;
        try
        {
            foreach (var node in NodeNameItems)
            {
                node.IsChecked = selected;
            }
        }
        finally
        {
            _suppressNodeSelection = false;
        }

        foreach (var node in NodeNameItems)
        {
            ApplyNodeSelection(node.Address, selected);
        }

        RebuildDisplay();
    }

    /// <summary>Recomputes the node checkboxes from the per-ID selection (mixed = null).</summary>
    private void RefreshNodeSelectionStates()
    {
        if (NodeNameItems.Count == 0)
        {
            return;
        }

        _suppressNodeSelection = true;
        try
        {
            foreach (var node in NodeNameItems)
            {
                var total = 0;
                var selected = 0;
                foreach (var filter in _allFrameFilters)
                {
                    var matches = filter.Extended &&
                        (node.IsBroadcast
                            ? IsBroadcastId(filter.Id)
                            : (int)(filter.Id & 0xFF) == node.Address);
                    if (!matches)
                    {
                        continue;
                    }

                    total++;
                    if (_idFilters.TryGetValue(filter.Id, out var visible) && visible)
                    {
                        selected++;
                    }
                }

                if (total == 0)
                {
                    // No IDs for this node yet: keep the user's explicit on/off choice.
                    continue;
                }

                node.IsChecked = selected == total
                    ? true
                    : selected == 0
                        ? false
                        : null;
            }
        }
        finally
        {
            _suppressNodeSelection = false;
        }
    }

    private void ResetNodeSelections()
    {
        _suppressNodeSelection = true;
        try
        {
            foreach (var node in NodeNameItems)
            {
                node.IsChecked = true;
            }
        }
        finally
        {
            _suppressNodeSelection = false;
        }
    }

    private void RefreshFilterLabels()
    {
        foreach (var item in _allFrameFilters)
        {
            item.Label = BuildFilterLabel(item.Id, item.Extended);
        }

        ApplySearchToFilterList();
    }

    private void UpdateFilterSummary()
    {
        var total = _idFilters.Count;
        var enabled = _idFilters.Values.Count(value => value);
        FilterSummary = $"{enabled} / {total} IDs shown";
    }

    private void ApplySearchToFilterList()
    {
        FrameFilters.Clear();
        foreach (var item in _allFrameFilters.OrderBy(filter => filter.Id))
        {
            if (MatchesSearch(item.Label))
            {
                FrameFilters.Add(item);
            }
        }
    }

    /// <summary>Keeps the visible ID list sorted by numeric frame ID.</summary>
    private void InsertFrameFilterSorted(FrameFilterItem item)
    {
        var index = 0;
        while (index < FrameFilters.Count && FrameFilters[index].Id < item.Id)
        {
            index++;
        }

        FrameFilters.Insert(index, item);
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
