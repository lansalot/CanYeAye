using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
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

            var files = desktop.Args?.Where(File.Exists).ToArray() ?? [];
            if (files.Length > 0)
            {
                window.Opened += (_, _) => window.OpenFiles(files);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnAboutClick(object? sender, EventArgs e)
    {
        var version = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "unknown";

        var about = new Window
        {
            Title = "About WinCAN",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "WinCAN", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = $"Version {version}", HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "CAN bus capture and analysis, based on SavvyCAN by Collin Kidder.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, TextAlignment = Avalonia.Media.TextAlignment.Center },
                },
            },
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            _ = about.ShowDialog(owner);
        }
        else
        {
            about.Show();
        }
    }
}
