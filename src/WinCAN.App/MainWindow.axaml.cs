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
}
