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
}
