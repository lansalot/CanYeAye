using Avalonia.Controls;
using Avalonia.Interactivity;
using WinCAN.App.Services;
using WinCAN.App.ViewModels;

namespace WinCAN.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainWindowViewModel
        {
            FileDialogs = new FileDialogService(() => this),
        };
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
