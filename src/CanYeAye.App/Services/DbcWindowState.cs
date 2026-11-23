using Avalonia;
using Avalonia.Controls;

namespace CanYeAye.App.Services;

/// <summary>Saved size and position of one window.</summary>
public sealed class WindowGeometry(double width = 0, double height = 0, double minWidth = 0, double minHeight = 0)
{
    public double Width { get; set; } = width;

    public double Height { get; set; } = height;

    public int X { get; set; } = int.MinValue;

    public int Y { get; set; } = int.MinValue;

    public void Apply(Window window)
    {
        if (Width > minWidth)
        {
            window.Width = Width;
        }

        if (Height > minHeight)
        {
            window.Height = Height;
        }

        if (X != int.MinValue && Y != int.MinValue)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(X, Y);
        }
    }

    public void Capture(Window window)
    {
        Width = window.Width;
        Height = window.Height;
        X = window.Position.X;
        Y = window.Position.Y;
    }
}

/// <summary>Saved geometry for the DBC editor and its child editors (SavvyCAN's DBC*Editor keys).</summary>
public sealed class DbcWindowState
{
    public WindowGeometry Editor { get; } = new(680, 800, 520, 480);

    public WindowGeometry Signal { get; } = new(1150, 930, 900, 700);

    public WindowGeometry Message { get; } = new(430, 620, 380, 520);

    public WindowGeometry Node { get; } = new(400, 210, 300, 180);

    public WindowGeometry NodeBase { get; } = new(480, 280, 380, 240);
}
