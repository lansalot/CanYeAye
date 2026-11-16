using System.Reflection;
using Avalonia.Controls;

namespace WinCAN.App.Views;

/// <summary>
/// The one About box, reachable from macOS's application menu and from Tools → About WinCAN…
/// so both show exactly the same form.
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {Version}";
    }

    /// <summary>Informational version without the source-revision suffix ("1.2.3", not "1.2.3+abc").</summary>
    public static string Version { get; } = typeof(AboutWindow).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        .Split('+')[0] ?? "unknown";

    /// <summary>Shows the About box modal over <paramref name="owner"/>, or standalone without one.</summary>
    public static void ShowFor(Window? owner)
    {
        var about = new AboutWindow();
        if (owner is null)
        {
            about.Show();
        }
        else
        {
            _ = about.ShowDialog(owner);
        }
    }
}
