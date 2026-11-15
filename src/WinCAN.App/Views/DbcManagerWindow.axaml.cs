using Avalonia.Controls;
using Avalonia.Interactivity;
using WinCAN.App.ViewModels;

namespace WinCAN.App.Views;

public partial class DbcManagerWindow : Window
{
    public DbcManagerWindow()
    {
        InitializeComponent();
    }

    public DbcManagerWindow(DbcManagerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
