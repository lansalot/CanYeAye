using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WinCAN.App.Views;

namespace WinCAN.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            var files = desktop.Args?.Where(File.Exists).ToArray() ?? [];
            if (files.Length > 0)
            {
                window.Opened += (_, _) => window.OpenFiles(files);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    // macOS application menu entry; Tools → About WinCAN… uses the same window.
    private void OnAboutClick(object? sender, EventArgs e) =>
        AboutWindow.ShowFor(
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow);
}
