using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using WinCAN.App.Services;
using WinCAN.App.ViewModels;
using WinCAN.Core.Settings;

namespace WinCAN.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly SettingsStore _settingsStore = new();
    private readonly AppSettings _settings;
    private Views.SendFrameWindow? _sendFrameWindow;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainWindowViewModel
        {
            FileDialogs = new FileDialogService(() => this),
        };

        _settings = _settingsStore.Load();
        Width = Math.Max(900, _settings.WindowWidth);
        Height = Math.Max(560, _settings.WindowHeight);
        _viewModel.SerialPort = _settings.LastSerialPort ?? _viewModel.SerialPort;
        _viewModel.BaudRate = _settings.LastBaudRate;
        _viewModel.Host = _settings.SocketCandHost ?? _viewModel.Host;
        _viewModel.Port = _settings.SocketCandPort;
        if (_settings.LastConnectionType is { } connectionType &&
            _viewModel.ConnectionTypes.Contains(connectionType))
        {
            _viewModel.SelectedConnectionType = connectionType;
        }

        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyLogVisibility();

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

            try
            {
                _settingsStore.Save(_settings with
                {
                    WindowWidth = (int)Width,
                    WindowHeight = (int)Height,
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
        };
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

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

        var column = FrameGrid.Columns.FirstOrDefault(c =>
            string.Equals(c.Header as string, header, StringComparison.Ordinal));
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

        if (FrameGrid.ContextMenu is { } menu)
        {
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                if (item.Tag is string)
                {
                    item.IsChecked = true;
                }
            }
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ShowLog))
        {
            ApplyLogVisibility();
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
