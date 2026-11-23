using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using CanYeAye.App.Services;
using CanYeAye.App.ViewModels;
using CanYeAye.Core.FileFormats;
using CanYeAye.Core.Settings;

namespace CanYeAye.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly SettingsStore _settingsStore = new();
    private readonly AppSettings _settings;
    private readonly bool _hasRestorePosition;
    private double _uiScale = 1.0;
    private Views.SendFrameWindow? _sendFrameWindow;
    private Views.DbcManagerWindow? _dbcManagerWindow;
    private Views.FrameFilterWindow? _frameFilterWindow;
    private Views.FrameFilterView? _frameFilterView;
    private FrameFilterHost _frameFilterHost = FrameFilterHost.None;
    private FrameFilterHost _lastFrameFilterHost = FrameFilterHost.Right;
    private Views.ProgressWindow? _progressWindow;
    private Views.FlowViewWindow? _flowViewWindow;
    private Views.FrameInfoWindow? _frameInfoWindow;

    private enum FrameFilterHost
    {
        None,
        Left,
        Right,
        Float,
        FullHeight,
    }

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainWindowViewModel
        {
            FileDialogs = new FileDialogService(() => this),
        };

        _settings = _settingsStore.Load();
        _uiScale = ClampScale(_settings.UiScale <= 0 ? 1.0 : _settings.UiScale);
        Width = Math.Max(900, _settings.WindowWidth) * _uiScale;
        Height = Math.Max(560, _settings.WindowHeight) * _uiScale;
        _hasRestorePosition = _settings.WindowX != int.MinValue && _settings.WindowY != int.MinValue;
        if (_hasRestorePosition)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        _viewModel.SerialPort = _settings.LastSerialPort ?? _viewModel.SerialPort;
        _viewModel.BaudRate = _settings.LastBaudRate;
        _viewModel.Bitrate = _settings.LastBusSpeed == 0 ? 500_000 : _settings.LastBusSpeed;
        _viewModel.EnsureBusSpeed(_viewModel.Bitrate);
        _viewModel.Host = _settings.SocketCandHost ?? _viewModel.Host;
        _viewModel.SocketCandPort = _settings.SocketCandPort;
        _viewModel.GvretTcpPort = _settings.GvretTcpPort;
        _viewModel.MqttPort = _settings.MqttPort;
        _viewModel.CanLogServerPort = _settings.CanLogServerPort;
        _viewModel.SelectedPcanChannel = Math.Clamp(_settings.PcanChannel, 1, 16);
        if (_settings.LastConnectionType is { } connectionType &&
            _viewModel.ConnectionTypes.Contains(connectionType))
        {
            _viewModel.SelectedConnectionType = connectionType;
        }

        foreach (var dbcPath in _settings.DbcFiles.Where(File.Exists))
        {
            _viewModel.LoadDbcFile(dbcPath);
        }

        foreach (var node in _settings.NodeNames)
        {
            _viewModel.NodeNames.SetName(node.Address, node.Name);
        }

        _viewModel.SyncNodeNameItems();

        _viewModel.NodeGraphLayout = _settings.NodeGraphLayout;
        _viewModel.NodeGraphZoom = _settings.NodeGraphZoom;
        _viewModel.NodeGraphPanX = _settings.NodeGraphPanX;
        _viewModel.NodeGraphPanY = _settings.NodeGraphPanY;
        _viewModel.NodeGraphWindowWidth = _settings.NodeGraphWindowWidth;
        _viewModel.NodeGraphWindowHeight = _settings.NodeGraphWindowHeight;
        _viewModel.NodeGraphWindowX = _settings.NodeGraphWindowX;
        _viewModel.NodeGraphWindowY = _settings.NodeGraphWindowY;

        _viewModel.FlowWindowWidth = _settings.FlowWindowWidth;
        _viewModel.FlowWindowHeight = _settings.FlowWindowHeight;
        _viewModel.FlowWindowX = _settings.FlowWindowX;
        _viewModel.FlowWindowY = _settings.FlowWindowY;

        _viewModel.DbcWindows.Editor.Width = _settings.DbcEditorWindowWidth;
        _viewModel.DbcWindows.Editor.Height = _settings.DbcEditorWindowHeight;
        _viewModel.DbcWindows.Editor.X = _settings.DbcEditorWindowX;
        _viewModel.DbcWindows.Editor.Y = _settings.DbcEditorWindowY;

        _viewModel.DbcWindows.Signal.Width = _settings.DbcSignalWindowWidth;
        _viewModel.DbcWindows.Signal.Height = _settings.DbcSignalWindowHeight;
        _viewModel.DbcWindows.Signal.X = _settings.DbcSignalWindowX;
        _viewModel.DbcWindows.Signal.Y = _settings.DbcSignalWindowY;

        _viewModel.DbcWindows.Message.Width = _settings.DbcMessageWindowWidth;
        _viewModel.DbcWindows.Message.Height = _settings.DbcMessageWindowHeight;
        _viewModel.DbcWindows.Message.X = _settings.DbcMessageWindowX;
        _viewModel.DbcWindows.Message.Y = _settings.DbcMessageWindowY;

        _viewModel.DbcWindows.Node.Width = _settings.DbcNodeWindowWidth;
        _viewModel.DbcWindows.Node.Height = _settings.DbcNodeWindowHeight;
        _viewModel.DbcWindows.Node.X = _settings.DbcNodeWindowX;
        _viewModel.DbcWindows.Node.Y = _settings.DbcNodeWindowY;

        _viewModel.DbcWindows.NodeBase.Width = _settings.DbcNodeBaseWindowWidth;
        _viewModel.DbcWindows.NodeBase.Height = _settings.DbcNodeBaseWindowHeight;
        _viewModel.DbcWindows.NodeBase.X = _settings.DbcNodeBaseWindowX;
        _viewModel.DbcWindows.NodeBase.Y = _settings.DbcNodeBaseWindowY;

        _viewModel.FrameInfoWindowWidth = _settings.FrameInfoWindowWidth;
        _viewModel.FrameInfoWindowHeight = _settings.FrameInfoWindowHeight;
        _viewModel.FrameInfoWindowX = _settings.FrameInfoWindowX;
        _viewModel.FrameInfoWindowY = _settings.FrameInfoWindowY;

        if (Enum.TryParse<FrameFilterHost>(_settings.FrameFilterHost, ignoreCase: true, out var savedFilterHost) &&
            savedFilterHost != FrameFilterHost.None)
        {
            _lastFrameFilterHost = savedFilterHost;
        }

        DataContext = _viewModel;
        BuildSaveFormatMenu();
        ConfigureMenus();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ScrollToEndRequested += OnScrollToEndRequested;
        _viewModel.RowExpansionChanged += OnRowExpansionChanged;

        _viewModel.AutoScrollWindow = _settings.AutoScrollWindow;
        _viewModel.OverwriteMode = _settings.OverwriteMode;
        _viewModel.InterpretFrames = _settings.InterpretFrames;
        _viewModel.KeepFiltersWhenClearing = _settings.KeepFiltersWhenClearing;
        ApplyColumnLayout(_settings);
        FrameGrid.RowHeight = _settings.RowsExpanded ? double.NaN : 24;
        ScaleHost.SizeChanged += OnScaleHostSizeChanged;
        ApplyScaleTransform();

        if (_hasRestorePosition)
        {
            Opened += OnOpenedRestorePosition;
        }

        Closed += async (_, _) =>
        {
            try
            {
                await _viewModel.ConnectionManager.DisconnectAllAsync();
            }
            catch
            {
                // best effort on shutdown
            }
        };

        Closing += (_, _) => SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings with
            {
                WindowWidth = (int)Math.Round(Width / _uiScale),
                WindowHeight = (int)Math.Round(Height / _uiScale),
                WindowX = Position.X,
                WindowY = Position.Y,
                UiScale = _uiScale,
                AutoScrollWindow = _viewModel.AutoScrollWindow,
                OverwriteMode = _viewModel.OverwriteMode,
                InterpretFrames = _viewModel.InterpretFrames,
                KeepFiltersWhenClearing = _viewModel.KeepFiltersWhenClearing,
                RowsExpanded = double.IsNaN(FrameGrid.RowHeight),
                Columns = CaptureColumns(),
                DbcFiles = _viewModel.Dbc.LoadedFiles
                    .Select(file => file.FilePath)
                    .Where(path => !string.IsNullOrEmpty(path))
                    .ToList(),
                LastConnectionType = _viewModel.SelectedConnectionType,
                LastSerialPort = _viewModel.SerialPort,
                LastBaudRate = _viewModel.BaudRate,
                LastBusSpeed = _viewModel.Bitrate,
                SocketCandHost = _viewModel.Host,
                SocketCandPort = _viewModel.SocketCandPort,
                GvretTcpPort = _viewModel.GvretTcpPort,
                MqttPort = _viewModel.MqttPort,
                CanLogServerPort = _viewModel.CanLogServerPort,
                PcanChannel = _viewModel.SelectedPcanChannel,
                NodeNames = _viewModel.NodeNames.Snapshot()
                    .OrderBy(pair => pair.Key)
                    .Select(pair => new CanYeAye.Core.Settings.NodeNameEntry(pair.Key, pair.Value))
                    .ToList(),
                NodeGraphLayout = _viewModel.NodeGraphLayout,
                NodeGraphZoom = _viewModel.NodeGraphZoom,
                NodeGraphPanX = _viewModel.NodeGraphPanX,
                NodeGraphPanY = _viewModel.NodeGraphPanY,
                NodeGraphWindowWidth = _viewModel.NodeGraphWindowWidth,
                NodeGraphWindowHeight = _viewModel.NodeGraphWindowHeight,
                NodeGraphWindowX = _viewModel.NodeGraphWindowX,
                NodeGraphWindowY = _viewModel.NodeGraphWindowY,
                FlowWindowWidth = _viewModel.FlowWindowWidth,
                FlowWindowHeight = _viewModel.FlowWindowHeight,
                FlowWindowX = _viewModel.FlowWindowX,
                FlowWindowY = _viewModel.FlowWindowY,
                FrameInfoWindowWidth = _viewModel.FrameInfoWindowWidth,
                FrameInfoWindowHeight = _viewModel.FrameInfoWindowHeight,
                FrameInfoWindowX = _viewModel.FrameInfoWindowX,
                FrameInfoWindowY = _viewModel.FrameInfoWindowY,
                DbcEditorWindowWidth = _viewModel.DbcWindows.Editor.Width,
                DbcEditorWindowHeight = _viewModel.DbcWindows.Editor.Height,
                DbcEditorWindowX = _viewModel.DbcWindows.Editor.X,
                DbcEditorWindowY = _viewModel.DbcWindows.Editor.Y,
                DbcSignalWindowWidth = _viewModel.DbcWindows.Signal.Width,
                DbcSignalWindowHeight = _viewModel.DbcWindows.Signal.Height,
                DbcSignalWindowX = _viewModel.DbcWindows.Signal.X,
                DbcSignalWindowY = _viewModel.DbcWindows.Signal.Y,
                DbcMessageWindowWidth = _viewModel.DbcWindows.Message.Width,
                DbcMessageWindowHeight = _viewModel.DbcWindows.Message.Height,
                DbcMessageWindowX = _viewModel.DbcWindows.Message.X,
                DbcMessageWindowY = _viewModel.DbcWindows.Message.Y,
                DbcNodeWindowWidth = _viewModel.DbcWindows.Node.Width,
                DbcNodeWindowHeight = _viewModel.DbcWindows.Node.Height,
                DbcNodeWindowX = _viewModel.DbcWindows.Node.X,
                DbcNodeWindowY = _viewModel.DbcWindows.Node.Y,
                DbcNodeBaseWindowWidth = _viewModel.DbcWindows.NodeBase.Width,
                DbcNodeBaseWindowHeight = _viewModel.DbcWindows.NodeBase.Height,
                DbcNodeBaseWindowX = _viewModel.DbcWindows.NodeBase.X,
                DbcNodeBaseWindowY = _viewModel.DbcWindows.NodeBase.Y,
                FrameFilterHost = _lastFrameFilterHost.ToString(),
            });
        }
        catch
        {
            // never block shutdown on settings I/O
        }
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private void OnAboutClick(object? sender, RoutedEventArgs e) => Views.AboutWindow.ShowFor(this);

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => ApplyScale(_uiScale - 0.1);

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => ApplyScale(_uiScale + 0.1);

    private void OnZoomResetClick(object? sender, RoutedEventArgs e) => ApplyScale(1.0);

    private void ConfigureMenus()
    {
        NativeMenuBridge.SetShortcut(OpenLogMenuItem, Key.O);
        NativeMenuBridge.SetShortcut(SaveLogMenuItem, Key.S);
        NativeMenuBridge.SetShortcut(ConnectionSettingsMenuItem, Key.K);
        NativeMenuBridge.SetShortcut(ZoomInMenuItem, Key.OemPlus);
        NativeMenuBridge.SetShortcut(ZoomOutMenuItem, Key.OemMinus);
        NativeMenuBridge.SetShortcut(ZoomResetMenuItem, Key.D0);

        // macOS: global menu bar; Exit is the application menu's Quit (Cmd+Q) there.
        NativeMenuBridge.Attach(this, MainMenu, ExitMenuItem);
    }

    private void BuildSaveFormatMenu()
    {
        SaveFormatMenu.Items.Clear();
        var selected = _viewModel.SelectedSaveFormat;
        for (var i = 0; i < LogFormatRegistry.SaveFilters.Count; i++)
        {
            var index = i;
            var filter = LogFormatRegistry.SaveFilters[i];
            var item = new MenuItem
            {
                Header = filter.Name,
                Tag = index,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = string.Equals(filter.Name, selected, StringComparison.Ordinal),
            };
            item.Click += OnSaveFormatClick;
            SaveFormatMenu.Items.Add(item);
        }
    }

    private void OnSaveFormatClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not int index ||
            index < 0 || index >= LogFormatRegistry.SaveFilters.Count)
        {
            return;
        }

        _viewModel.SelectedSaveFormat = LogFormatRegistry.SaveFilters[index].Name;
        UpdateSaveFormatChecks();
    }

    private void UpdateSaveFormatChecks()
    {
        var selected = _viewModel.SelectedSaveFormat;
        foreach (var item in SaveFormatMenu.Items.OfType<MenuItem>())
        {
            if (item.Tag is int index && index >= 0 && index < LogFormatRegistry.SaveFilters.Count)
            {
                item.IsChecked = string.Equals(
                    LogFormatRegistry.SaveFilters[index].Name,
                    selected,
                    StringComparison.Ordinal);
            }
        }
    }

    private void ApplyScale(double scale)
    {
        var clamped = ClampScale(scale);
        if (Math.Abs(clamped - _uiScale) < 0.0001)
        {
            return;
        }

        var previous = _uiScale;
        _uiScale = clamped;
        Width = Math.Max(600, Width / previous * clamped);
        Height = Math.Max(400, Height / previous * clamped);
        ApplyScaleTransform();
    }

    private void ApplyScaleTransform()
    {
        _viewModel.UiScalePercent = (int)Math.Round(_uiScale * 100);
        if (ScaleHost.Bounds.Width <= 0 || ScaleHost.Bounds.Height <= 0)
        {
            return;
        }

        RootContent.RenderTransform = new ScaleTransform(_uiScale, _uiScale);
        RootContent.RenderTransformOrigin = RelativePoint.TopLeft;
        RootContent.Width = ScaleHost.Bounds.Width / _uiScale;
        RootContent.Height = ScaleHost.Bounds.Height / _uiScale;
    }

    private void OnScaleHostSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyScaleTransform();

    private static double ClampScale(double scale) =>
        Math.Clamp(Math.Round(scale * 10, MidpointRounding.AwayFromZero) / 10.0, 0.7, 2.0);

    private void OnOpenedRestorePosition(object? sender, EventArgs e)
    {
        var x = _settings.WindowX;
        var y = _settings.WindowY;
        var screen = Screens.ScreenFromPoint(new PixelPoint(x, y)) ?? Screens.Primary;
        if (screen is not null)
        {
            x = Math.Clamp(x, screen.WorkingArea.X - 50, screen.WorkingArea.X + screen.WorkingArea.Width - 100);
            y = Math.Clamp(y, screen.WorkingArea.Y - 20, screen.WorkingArea.Y + screen.WorkingArea.Height - 100);
        }

        Position = new PixelPoint(x, y);
    }

    /// <summary>Loads logs/DBC files passed on the command line.</summary>
    public void OpenFiles(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (path.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase))
            {
                _viewModel.LoadDbcFile(path);
            }
            else
            {
                _ = _viewModel.LoadLogFileAsync(path);
            }
        }
    }

    private async void OnConnectionClick(object? sender, RoutedEventArgs e)
    {
        var window = new Views.ConnectionWindow(_viewModel);
        await window.ShowDialog(this);
    }

    private void OnSendFrameClick(object? sender, RoutedEventArgs e)
    {
        if (_sendFrameWindow is { IsVisible: true })
        {
            _sendFrameWindow.Activate();
            return;
        }

        _sendFrameWindow = new Views.SendFrameWindow(_viewModel);
        _sendFrameWindow.Closed += (_, _) => _sendFrameWindow = null;
        _sendFrameWindow.Show(this);
    }

    private void OnFrameFilterClick(object? sender, RoutedEventArgs e)
    {
        if (_frameFilterHost != FrameFilterHost.None)
        {
            if (_frameFilterWindow is { IsVisible: true })
            {
                _frameFilterWindow.Activate();
            }

            return;
        }

        ShowFrameFilterFrame();
    }

    /// <summary>Shows the filter where it was last used (docked by default, floating only on request).</summary>
    private void ShowFrameFilterFrame()
    {
        EnsureFilterView();
        ShowFrameFilter(_lastFrameFilterHost == FrameFilterHost.None
            ? FrameFilterHost.Right
            : _lastFrameFilterHost);
    }

    private void EnsureFilterView()
    {
        if (_frameFilterView is not null)
        {
            return;
        }

        _frameFilterView = new Views.FrameFilterView { DataContext = _viewModel };
        _frameFilterView.DockLeftRequested += (_, _) => ShowFrameFilter(FrameFilterHost.Left);
        _frameFilterView.DockRightRequested += (_, _) => ShowFrameFilter(FrameFilterHost.Right);
        _frameFilterView.FloatRequested += (_, _) => ShowFrameFilter(FrameFilterHost.Float);
        _frameFilterView.FullHeightRequested += (_, _) => ShowFrameFilter(FrameFilterHost.FullHeight);
        _frameFilterView.CloseRequested += (_, _) => HideFrameFilter();
    }

    private void ShowFrameFilter(FrameFilterHost host)
    {
        EnsureFilterView();
        DetachFilterView();
        if (_frameFilterView is null)
        {
            return;
        }

        switch (host)
        {
            case FrameFilterHost.Left:
                FilterDockLeft.Child = _frameFilterView;
                FilterDockLeft.IsVisible = true;
                break;

            case FrameFilterHost.Right:
                FilterDockRight.Child = _frameFilterView;
                FilterDockRight.IsVisible = true;
                break;

            case FrameFilterHost.Float:
            case FrameFilterHost.FullHeight:
            {
                var window = new Views.FrameFilterWindow(_frameFilterView);
                window.Closed += (_, _) =>
                {
                    window.Detach();
                    if (ReferenceEquals(_frameFilterWindow, window))
                    {
                        _frameFilterWindow = null;
                        _frameFilterHost = FrameFilterHost.None;
                    }
                };

                if (host == FrameFilterHost.FullHeight)
                {
                    var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
                    if (screen is not null)
                    {
                        var scaling = RenderScaling <= 0 ? 1.0 : RenderScaling;
                        window.Width = 360;
                        window.Height = Math.Max(320, screen.WorkingArea.Height / scaling);
                        window.Position = new PixelPoint(
                            Math.Max(screen.WorkingArea.X + screen.WorkingArea.Width - 380, screen.WorkingArea.X),
                            screen.WorkingArea.Y);
                    }
                }
                else
                {
                    window.Width = 350;
                    window.Height = 650;
                    var scaling = RenderScaling <= 0 ? 1.0 : RenderScaling;
                    var x = Position.X + (int)Math.Round(Bounds.Width * scaling)
                            - (int)Math.Round(window.Width * scaling) - 40;
                    var y = Position.Y + 60;
                    window.Position = new PixelPoint(Math.Max(0, x), Math.Max(0, y));
                }

                _frameFilterWindow = window;
                window.Show(this);
                break;
            }
        }

        _frameFilterHost = host;
        _lastFrameFilterHost = host;
    }

    /// <summary>Removes the filter from whichever host currently owns it.</summary>
    private void DetachFilterView()
    {
        if (_frameFilterView is null)
        {
            return;
        }

        if (ReferenceEquals(FilterDockLeft.Child, _frameFilterView))
        {
            FilterDockLeft.Child = null;
        }

        if (ReferenceEquals(FilterDockRight.Child, _frameFilterView))
        {
            FilterDockRight.Child = null;
        }

        FilterDockLeft.IsVisible = false;
        FilterDockRight.IsVisible = false;

        var window = _frameFilterWindow;
        _frameFilterWindow = null;
        _frameFilterHost = FrameFilterHost.None;
        if (window is not null)
        {
            window.Detach();
            window.Close();
        }
    }

    private void HideFrameFilter()
    {
        DetachFilterView();
        _frameFilterHost = FrameFilterHost.None;
    }

    private void OnDbcManagerClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.FileDialogs is null)
        {
            return;
        }

        if (_dbcManagerWindow is { IsVisible: true })
        {
            _dbcManagerWindow.Activate();
            return;
        }

        var viewModel = new DbcManagerViewModel(_viewModel.Dbc, _viewModel.FileDialogs, _viewModel.OnDbcChanged, _viewModel.DbcWindows);
        _dbcManagerWindow = new Views.DbcManagerWindow(viewModel);
        _dbcManagerWindow.Closed += (_, _) => _dbcManagerWindow = null;
        _dbcManagerWindow.Show(this);
    }

    private void OnScriptingClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.FileDialogs is null)
        {
            return;
        }

        var viewModel = new ScriptingWindowViewModel(_viewModel.ConnectionManager, _viewModel.FileDialogs);
        new Views.ScriptingWindow(viewModel).Show(this);
    }

    private void OnObd2Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.FileDialogs is null)
        {
            return;
        }

        var viewModel = new Obd2WindowViewModel(_viewModel.ConnectionManager, _viewModel.FileDialogs);
        new Views.Obd2Window(viewModel).Show(this);
    }

    private void OnSnifferClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = new SnifferWindowViewModel(
            _viewModel.ConnectionManager,
            _viewModel.FilterStateSnapshot());
        new Views.SnifferWindow(viewModel).Show(this);
    }

    private void OnNodeGraphClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = new NodeGraphViewModel(
            _viewModel.NodeNames,
            _viewModel.FrameSnapshot,
            _viewModel.ConnectionManager);
        var window = new Views.NodeGraphWindow(viewModel);
        window.ApplyLayout(_viewModel.NodeGraphLayout);
        window.ApplyView(_viewModel.NodeGraphZoom, _viewModel.NodeGraphPanX, _viewModel.NodeGraphPanY);

        if (_viewModel.NodeGraphWindowWidth > 0)
        {
            window.Width = _viewModel.NodeGraphWindowWidth;
            window.Height = _viewModel.NodeGraphWindowHeight;
        }

        if (_viewModel.NodeGraphWindowX != int.MinValue && _viewModel.NodeGraphWindowY != int.MinValue)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(_viewModel.NodeGraphWindowX, _viewModel.NodeGraphWindowY);
        }

        window.Closed += (_, _) =>
        {
            _viewModel.NodeGraphLayout = window.CaptureLayout().ToList();
            var view = window.CaptureView();
            _viewModel.NodeGraphZoom = view.Zoom;
            _viewModel.NodeGraphPanX = view.PanX;
            _viewModel.NodeGraphPanY = view.PanY;
            _viewModel.NodeGraphWindowWidth = window.Width;
            _viewModel.NodeGraphWindowHeight = window.Height;
            _viewModel.NodeGraphWindowX = window.Position.X;
            _viewModel.NodeGraphWindowY = window.Position.Y;
        };

        window.Show(this);
    }

    private void OnFlowViewClick(object? sender, RoutedEventArgs e)
    {
        if (_flowViewWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        var viewModel = new FlowViewWindowViewModel(
            _viewModel.FrameSnapshot,
            _viewModel.DescribeFrameId,
            _viewModel.ConnectionManager);
        var window = new Views.FlowViewWindow(viewModel);

        if (_viewModel.FlowWindowWidth > 0)
        {
            window.Width = _viewModel.FlowWindowWidth;
            window.Height = _viewModel.FlowWindowHeight;
        }

        if (_viewModel.FlowWindowX != int.MinValue && _viewModel.FlowWindowY != int.MinValue)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(_viewModel.FlowWindowX, _viewModel.FlowWindowY);
        }

        void OnCaptureReplaced(object? captureSender, EventArgs args) => viewModel.ReloadCapture();
        _viewModel.CaptureReplaced += OnCaptureReplaced;

        window.Closed += (_, _) =>
        {
            _viewModel.CaptureReplaced -= OnCaptureReplaced;
            _viewModel.FlowWindowWidth = window.Width;
            _viewModel.FlowWindowHeight = window.Height;
            _viewModel.FlowWindowX = window.Position.X;
            _viewModel.FlowWindowY = window.Position.Y;
            _flowViewWindow = null;
        };

        _flowViewWindow = window;
        window.Show(this);
    }

    private void OnFrameInfoClick(object? sender, RoutedEventArgs e)
    {
        if (_frameInfoWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        // Snapshot semantics: the window analyses the capture as it is right now and never
        // subscribes to live frames (unlike Flow View).
        var viewModel = new FrameInfoWindowViewModel(
            _viewModel.FrameSnapshot(),
            _viewModel.DescribeFrameId,
            _viewModel.Dbc,
            _viewModel.FileDialogs);
        var window = new Views.FrameInfoWindow(viewModel);

        if (_viewModel.FrameInfoWindowWidth > 0)
        {
            window.Width = _viewModel.FrameInfoWindowWidth;
            window.Height = _viewModel.FrameInfoWindowHeight;
        }

        if (_viewModel.FrameInfoWindowX != int.MinValue && _viewModel.FrameInfoWindowY != int.MinValue)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(_viewModel.FrameInfoWindowX, _viewModel.FrameInfoWindowY);
        }

        window.Closed += (_, _) =>
        {
            _viewModel.FrameInfoWindowWidth = window.Width;
            _viewModel.FrameInfoWindowHeight = window.Height;
            _viewModel.FrameInfoWindowX = window.Position.X;
            _viewModel.FrameInfoWindowY = window.Position.Y;
            _frameInfoWindow = null;
        };

        _frameInfoWindow = window;
        window.Show(this);
    }

    private void OnToggleColumnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string header)
        {
            return;
        }

        var column = FindColumn(header);
        if (column is not null)
        {
            column.IsVisible = item.IsChecked;
        }
    }

    private void OnShowAllColumnsClick(object? sender, RoutedEventArgs e)
    {
        foreach (var column in FrameGrid.Columns)
        {
            column.IsVisible = true;
        }

        SyncColumnMenu();
    }

    private void OnScrollToEndRequested(object? sender, EventArgs e)
    {
        if (_viewModel.Frames.Count == 0)
        {
            return;
        }

        var last = _viewModel.Frames[^1];
        FrameGrid.ScrollIntoView(last, FrameGrid.Columns[0]);
    }

    private void OnRowExpansionChanged(object? sender, bool expanded)
    {
        FrameGrid.RowHeight = expanded ? double.NaN : 24;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.OverwriteMode):
                SetColumnVisibility("Count", _viewModel.OverwriteMode);
                break;
            case nameof(MainWindowViewModel.IsLoading):
                ApplyProgressVisibility();
                break;
        }
    }

    private DataGridColumn? FindColumn(string header) =>
        FrameGrid.Columns.FirstOrDefault(column =>
            string.Equals(column.Header as string, header, StringComparison.Ordinal));

    private void SetColumnVisibility(string header, bool visible)
    {
        var column = FindColumn(header);
        if (column is not null)
        {
            column.IsVisible = visible;
        }

        SyncColumnMenu();
    }

    private void SyncColumnMenu()
    {
        if (FrameGrid.ContextMenu is not { } menu)
        {
            return;
        }

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is not string header)
            {
                continue;
            }

            var column = FindColumn(header);
            if (column is not null)
            {
                item.IsChecked = column.IsVisible;
            }
        }
    }

    private void ApplyColumnLayout(AppSettings settings)
    {
        if (settings.Columns.Count == 0)
        {
            return;
        }

        foreach (var saved in settings.Columns)
        {
            var column = FindColumn(saved.Header);
            if (column is null)
            {
                continue;
            }

            column.IsVisible = saved.Visible;
            if (saved.Width > 0)
            {
                column.Width = new DataGridLength(saved.Width, ParseUnit(saved.Unit));
            }
        }

        var ordered = settings.Columns
            .Where(saved => saved.Visible)
            .OrderBy(saved => saved.DisplayIndex)
            .Select(saved => FindColumn(saved.Header))
            .Where(column => column is not null)
            .ToList();

        // Columns added after the layout was saved (e.g. Nodes) keep their default
        // position: slot them in after the ID column and keep them visible.
        var savedHeaders = settings.Columns
            .Select(saved => saved.Header)
            .ToHashSet(StringComparer.Ordinal);
        var unlisted = FrameGrid.Columns
            .Where(column => !savedHeaders.Contains((column.Header as string) ?? string.Empty))
            .ToList();
        var idIndex = ordered.FindIndex(column =>
            string.Equals(column!.Header as string, "ID", StringComparison.Ordinal));
        var finalOrder = ordered.ToList();
        finalOrder.InsertRange(idIndex >= 0 ? idIndex + 1 : finalOrder.Count, unlisted);

        for (var i = 0; i < finalOrder.Count; i++)
        {
            try
            {
                finalOrder[i]!.DisplayIndex = i;
            }
            catch (ArgumentOutOfRangeException)
            {
                // index already claimed; keep the current order
            }
        }

        SyncColumnMenu();
    }

    private List<ColumnLayout> CaptureColumns() =>
        FrameGrid.Columns
            .Select(column => new ColumnLayout(
                (column.Header as string) ?? string.Empty,
                column.Width.Value,
                column.Width.UnitType.ToString(),
                column.DisplayIndex,
                column.IsVisible))
            .ToList();

    private static DataGridLengthUnitType ParseUnit(string unit) =>
        Enum.TryParse<DataGridLengthUnitType>(unit, ignoreCase: true, out var parsed)
            ? parsed
            : DataGridLengthUnitType.Pixel;

    private void ApplyProgressVisibility()
    {
        if (_viewModel.IsLoading)
        {
            if (_progressWindow is null)
            {
                _progressWindow = new Views.ProgressWindow(_viewModel);
                _progressWindow.Closed += (_, _) => _progressWindow = null;
                _progressWindow.Show(this);
            }
        }
        else
        {
            _progressWindow?.Close();
            _progressWindow = null;
        }
    }

}
