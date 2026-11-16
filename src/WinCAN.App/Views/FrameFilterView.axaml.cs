using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WinCAN.App.Views;

/// <summary>
/// The frame-filtering UI. It lives inside the main window when docked (left/right) and is
/// hosted by <see cref="FrameFilterWindow"/> only in float mode.
/// </summary>
public partial class FrameFilterView : UserControl
{
    public FrameFilterView()
    {
        InitializeComponent();
    }

    public event EventHandler? DockLeftRequested;

    public event EventHandler? DockRightRequested;

    public event EventHandler? FloatRequested;

    public event EventHandler? FullHeightRequested;

    public event EventHandler? CloseRequested;

    private void OnDockLeftClick(object? sender, RoutedEventArgs e) =>
        DockLeftRequested?.Invoke(this, EventArgs.Empty);

    private void OnDockRightClick(object? sender, RoutedEventArgs e) =>
        DockRightRequested?.Invoke(this, EventArgs.Empty);

    private void OnFloatClick(object? sender, RoutedEventArgs e) =>
        FloatRequested?.Invoke(this, EventArgs.Empty);

    private void OnFullHeightClick(object? sender, RoutedEventArgs e) =>
        FullHeightRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);
}
