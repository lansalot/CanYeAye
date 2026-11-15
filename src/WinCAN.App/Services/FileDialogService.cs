using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace WinCAN.App.Services;

/// <summary>Avalonia storage-provider dialogs; keeps the view models free of window types.</summary>
public sealed class FileDialogService(Func<TopLevel?> topLevel) : IFileDialogService
{
    private static readonly FilePickerFileType LogFiles = new("CAN log files")
    {
        Patterns =
        [
            "*.csv", "*.CSV", "*.log", "*.LOG", "*.txt", "*.TXT", "*.trc", "*.TRC",
            "*.asc", "*.ASC", "*.crt", "*.CRT", "*.crtd", "*.CRTD", "*.can", "*.CAN",
            "*.avc", "*.AVC", "*.evc", "*.EVC", "*.qcc", "*.QCC", "*.trace", "*.TRACE",
            "*.blf", "*.BLF", "*.pcap", "*.PCAP", "*.pcapng", "*.PCAPNG",
        ],
    };

    private static readonly FilePickerFileType DbcFiles = new("DBC files")
    {
        Patterns = ["*.dbc", "*.DBC"],
    };

    private static readonly FilePickerFileType AllFiles = new("All files") { Patterns = ["*"] };

    public async Task<string?> PickOpenLogAsync()
    {
        var storage = topLevel()?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open CAN log",
            AllowMultiple = false,
            FileTypeFilter = [LogFiles, AllFiles],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveLogAsync(string suggestedName)
    {
        var storage = topLevel()?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var extension = Path.GetExtension(suggestedName);
        if (extension.Length == 0)
        {
            extension = ".csv";
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save CAN log",
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName) + extension,
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = [LogFiles, AllFiles],
        });

        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenDbcAsync()
    {
        var storage = topLevel()?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open DBC",
            AllowMultiple = false,
            FileTypeFilter = [DbcFiles, AllFiles],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var window = new Window
        {
            Title = title,
            Width = 460,
            Height = 190,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new Button
                    {
                        Content = "OK",
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Width = 90,
                    },
                },
            },
        };

        if (window.Content is StackPanel panel && panel.Children[1] is Button ok)
        {
            ok.Click += (_, _) => window.Close();
        }

        var owner = topLevel() as Window;
        if (owner is not null && owner.IsVisible)
        {
            await window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }
    }
}
