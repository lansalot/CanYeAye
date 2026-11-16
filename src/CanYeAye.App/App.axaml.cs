using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CanYeAye.App.Views;

namespace CanYeAye.App;

public partial class App : Application
{
    /// <summary>How long the splash card stays up, measured from the moment it is shown.</summary>
    internal static readonly TimeSpan SplashDuration = TimeSpan.FromSeconds(5);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The splash goes up first and stays topmost, so the window that builds underneath
            // it is already there when the five seconds are up.
            var splash = new SplashWindow();
            var splashShown = Stopwatch.StartNew();
            splash.Show();

            var window = new MainWindow();
            desktop.MainWindow = window;

            var files = desktop.Args?.Where(File.Exists).ToArray() ?? [];
            if (files.Length > 0)
            {
                window.Opened += (_, _) => window.OpenFiles(files);
            }

            window.Closed += (_, _) => CloseSplash(splash);
            var remaining = SplashDuration - splashShown.Elapsed;
            DispatcherTimer.RunOnce(
                () => CloseSplash(splash),
                remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void CloseSplash(SplashWindow splash)
    {
        if (splash.IsVisible)
        {
            splash.Close();
        }
    }

    // macOS application menu entry; Tools → About CanYeAye… uses the same window.
    private void OnAboutClick(object? sender, EventArgs e) =>
        AboutWindow.ShowFor(
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow);
}
