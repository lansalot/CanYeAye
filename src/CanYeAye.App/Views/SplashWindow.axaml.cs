using Avalonia.Controls;

namespace CanYeAye.App.Views;

/// <summary>
/// Frameless startup card shown over the main window while it loads, then dismissed after
/// <see cref="App.SplashDuration"/> by <see cref="App"/>.
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AboutWindow.Version}";
    }
}
