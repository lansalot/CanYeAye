using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using WinCAN.App.Services;
using WinCAN.App.ViewModels;
using WinCAN.Core.Settings;

namespace WinCAN.App;

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
    private Views.ProgressWindow? _progressWindow;

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
        _viewModel.Host = _settings.SocketCandHost ?? _viewModel.Host;
        _viewModel.Port = _settings.SocketCandPort;
        if (_settings.LastConnectionType is { } connectionType &&
            _viewModel.ConnectionTypes.Contains(connectionType))
        {
            _viewModel.SelectedConnectionType = connectionType;
        }

        foreach (var dbcPath in _settings.DbcFiles.Where(File.Exists))
        {
            _viewModel.LoadDbcFile(dbcPath);
        }

        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ScrollToEndRequested += OnScrollToEndRequested;
        _viewModel.RowExpansionChanged += OnRowExpansionChanged;

        _viewModel.ShowLog = _settings.ShowLog;
        _viewModel.AutoScrollWindow = _settings.AutoScrollWindow;
        _viewModel.OverwriteMode = _settings.OverwriteMode;
        _viewModel.InterpretFrames = _settings.InterpretFrames;
        _viewModel.KeepFiltersWhenClearing = _settings.KeepFiltersWhenClearing;
        ApplyColumnLayout(_settings);
        FrameGrid.RowHeight = _settings.RowsExpanded ? double.NaN : 24;
        ApplyLogVisibility();
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
                ShowLog = _viewModel.ShowLog,
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
                SocketCandHost = _viewModel.Host,
                SocketCandPort = _viewModel.Port,
            });
        }
        catch
        {
            // never block shutdown on settings I/O
        }
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => ApplyScale(_uiScale - 0.1);

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => ApplyScale(_uiScale + 0.1);

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
        RootScale.LayoutTransform = new ScaleTransform(_uiScale, _uiScale);
        _viewModel.UiScalePercent = (int)Math.Round(_uiScale * 100);
    }

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
        if (_frameFilterWindow is { IsVisible: true })
        {
            _frameFilterWindow.Activate();
            return;
        }

        _frameFilterWindow = new Views.FrameFilterWindow(_viewModel);
        _frameFilterWindow.Closed += (_, _) => _frameFilterWindow = null;
        _frameFilterWindow.Show(this);
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

        var viewModel = new DbcManagerViewModel(_viewModel.Dbc, _viewModel.FileDialogs, _viewModel.OnDbcChanged);
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
        var viewModel = new SnifferWindowViewModel(_viewModel.ConnectionManager);
        new Views.SnifferWindow(viewModel).Show(this);
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
            case nameof(MainWindowViewModel.ShowLog):
                ApplyLogVisibility();
                break;
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

        for (var i = 0; i < ordered.Count; i++)
        {
            try
            {
                ordered[i]!.DisplayIndex = i;
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

    private void ApplyLogVisibility()
    {
        if (FrameArea.RowDefinitions.Count < 3)
        {
            return;
        }

        var visible = _viewModel.ShowLog;
        FrameArea.RowDefinitions[1].Height = new GridLength(visible ? 4 : 0);
        FrameArea.RowDefinitions[2].Height = new GridLength(visible ? 180 : 0);
    }
}
