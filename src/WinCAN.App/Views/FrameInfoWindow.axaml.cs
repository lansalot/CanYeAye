using Avalonia.Controls;
using WinCAN.App.ViewModels;

namespace WinCAN.App.Views;

/// <summary>
/// Detailed Frame Information (re/frameinfowindow). The view model holds a snapshot of the
/// capture, so this window deliberately never follows live traffic.
/// </summary>
public partial class FrameInfoWindow : Window
{
    public FrameInfoWindow()
    {
        InitializeComponent();
    }

    public FrameInfoWindow(FrameInfoWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
