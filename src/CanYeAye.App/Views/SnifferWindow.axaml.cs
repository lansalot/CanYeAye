using Avalonia;
using Avalonia.Controls;
using CanYeAye.App.ViewModels;

namespace CanYeAye.App.Views;

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

    /// <summary>Keeps the fixed header aligned while the rows scroll sideways (CAN FD).</summary>
    private void OnGridScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer grid && this.FindControl<ScrollViewer>("HeaderScroll") is { } header)
        {
            header.Offset = new Vector(grid.Offset.X, 0);
        }
    }
}
