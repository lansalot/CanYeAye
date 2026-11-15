using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

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

            if (desktop.Args is { Length: > 0 } args && File.Exists(args[0]))
            {
                var path = args[0];
                window.Opened += (_, _) => window.OpenLogFile(path);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}