using Avalonia.Controls;
using WinCAN.App.ViewModels;

namespace WinCAN.App.Views;

public partial class SnifferWindow : Window
{
    private readonly SnifferWindowViewModel? _viewModel;

    public SnifferWindow()
    {
        InitializeComponent();
    }

    public SnifferWindow(SnifferWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Closed += (_, _) => _viewModel.Dispose();
    }
}
