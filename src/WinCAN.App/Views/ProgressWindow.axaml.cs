using Avalonia.Controls;
using WinCAN.App.ViewModels;

namespace WinCAN.App.Views;

public partial class ProgressWindow : Window
{
    public ProgressWindow()
    {
        InitializeComponent();
    }

    public ProgressWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
