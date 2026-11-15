using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using WinCAN.App.ViewModels;

namespace WinCAN.App.Views;

/// <summary>
/// Floating frame-ID filter list. Draggable/resizable as a normal tool window with quick
/// docking to either side of the owner, screen-height, and floating presets.
/// </summary>
public partial class FrameFilterWindow : Window
{
    public FrameFilterWindow()
    {
        InitializeComponent();
    }

    public FrameFilterWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Opened += (_, _) => DockRight();
    }

    private void OnDockLeftClick(object? sender, RoutedEventArgs e) => DockLeft();

    private void OnDockRightClick(object? sender, RoutedEventArgs e) => DockRight();

    private void OnFullHeightClick(object? sender, RoutedEventArgs e) => FillScreenHeight();

    private void OnFloatClick(object? sender, RoutedEventArgs e) => Float();

    private void DockLeft()
    {
        if (Owner is not Window owner)
        {
            return;
        }

        Width = 320;
        Height = Math.Max(300, owner.Bounds.Height);
        Position = new PixelPoint(owner.Position.X, owner.Position.Y);
    }

    private void DockRight()
    {
        if (Owner is not Window owner)
        {
            return;
        }

        var ownerScaling = owner.RenderScaling <= 0 ? 1.0 : owner.RenderScaling;
        var ownerWidthPixels = (int)Math.Round(owner.Bounds.Width * ownerScaling);
        var ownWidthPixels = (int)Math.Round(320 * (RenderScaling <= 0 ? 1.0 : RenderScaling));

        Width = 320;
        Height = Math.Max(300, owner.Bounds.Height);
        Position = new PixelPoint(
            owner.Position.X + ownerWidthPixels - ownWidthPixels,
            owner.Position.Y);
    }

    private void FillScreenHeight()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scaling = RenderScaling <= 0 ? 1.0 : RenderScaling;
        Height = Math.Max(300, screen.WorkingArea.Height / scaling);
        Position = new PixelPoint(Position.X, screen.WorkingArea.Y);
    }

    private void Float()
    {
        Width = 340;
        Height = 620;

        if (Owner is not Window owner)
        {
            return;
        }

        var ownerScaling = owner.RenderScaling <= 0 ? 1.0 : owner.RenderScaling;
        var ownScaling = RenderScaling <= 0 ? 1.0 : RenderScaling;
        var x = owner.Position.X + (int)Math.Round(owner.Bounds.Width * ownerScaling)
                - (int)Math.Round(Width * ownScaling) - 40;
        var y = owner.Position.Y + 60;
        Position = new PixelPoint(Math.Max(0, x), Math.Max(0, y));
    }
}
