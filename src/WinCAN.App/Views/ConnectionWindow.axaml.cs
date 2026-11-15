using Avalonia.Controls;
using Avalonia.Interactivity;
using WinCAN.App.ViewModels;

namespace WinCAN.App.Views;

public partial class ConnectionWindow : Window
{
    public ConnectionWindow()
    {
        InitializeComponent();
    }

    public ConnectionWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
