using Avalonia.Controls;
using Avalonia.Interactivity;
using CanYeAye.App.ViewModels;

namespace CanYeAye.App.Views;

public partial class SendFrameWindow : Window
{
    public SendFrameWindow()
    {
        InitializeComponent();
    }

    public SendFrameWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
