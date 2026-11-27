using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace CanYeAye.App.Services;

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

    private static readonly FilePickerFileType ScriptFiles = new("JavaScript files")
    {
        Patterns = ["*.js", "*.JS"],
    };

    private static readonly FilePickerFileType AllFiles = new("All files") { Patterns = ["*"] };

    private static readonly FilePickerFileType NodeNameFiles = new("J1939 node names")
    {
        Patterns = ["*.txt", "*.TXT", "*.nodes", "*.NODES"],
    };

    private static readonly FilePickerFileType TextFiles = new("Text files")
    {
        Patterns = ["*.txt", "*.TXT"],
    };

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
        var files = await PickOpenDbcsAsync();
        return files.Count > 0 ? files[0] : null;
    }

    public async Task<IReadOnlyList<string>> PickOpenDbcsAsync()
    {
        var storage = topLevel()?.StorageProvider;
        if (storage is null)
        {
            return [];
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open DBC files",
            AllowMultiple = true,
            FileTypeFilter = [DbcFiles, AllFiles],
        });

        return files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!)
            .ToArray();
    }

    public async Task<string?> PickOpenScriptAsync()
    {
        var storage = topLevel()?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open script",
            AllowMultiple = false,
            FileTypeFilter = [ScriptFiles, AllFiles],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveScriptAsync(string suggestedName)
    {
        var storage = topLevel()?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save script",
            SuggestedFileName = suggestedName,
            DefaultExtension = "js",
            FileTypeChoices = [ScriptFiles, AllFiles],
        });

        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenNodeNamesAsync()
    {
        var storage = topLevel()?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open J1939 node names",
            AllowMultiple = false,
            FileTypeFilter = [NodeNameFiles, AllFiles],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveNodeNamesAsync(string suggestedName)
    {
        var storage = topLevel()?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save J1939 node names",
            SuggestedFileName = suggestedName,
            DefaultExtension = "txt",
            FileTypeChoices = [NodeNameFiles, AllFiles],
        });

        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickSaveTextAsync(string suggestedName)
    {
        var storage = topLevel()?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save frame details",
            SuggestedFileName = suggestedName,
            DefaultExtension = "txt",
            FileTypeChoices = [TextFiles, AllFiles],
        });

        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickSaveDbcAsync(string suggestedName)
    {
        var storage = topLevel()?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save DBC file",
            SuggestedFileName = suggestedName,
            DefaultExtension = "dbc",
            FileTypeChoices = [DbcFiles, AllFiles],
        });

        return file?.TryGetLocalPath();
    }

    public Task<bool> ConfirmAsync(string title, string message) =>
        ConfirmAsync(title, message, "OK", "Cancel");

    public async Task<bool> ConfirmAsync(string title, string message, string acceptText, string declineText)
    {
        var accepted = false;
        var window = new Window
        {
            Title = title,
            Width = 560,
            MinHeight = 220,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children =
                        {
                            new Button
                            {
                                Content = declineText,
                                MinWidth = 140,
                                Padding = new Avalonia.Thickness(16, 8),
                            },
                            new Button
                            {
                                Content = acceptText,
                                MinWidth = 140,
                                Padding = new Avalonia.Thickness(16, 8),
                            },
                        },
                    },
                },
            },
        };

        if (window.Content is StackPanel panel && panel.Children[1] is StackPanel buttons)
        {
            ((Button)buttons.Children[0]).Click += (_, _) => window.Close();
            ((Button)buttons.Children[1]).Click += (_, _) =>
            {
                accepted = true;
                window.Close();
            };
        }

        var owner = topLevel() as Window;
        if (owner is not null && owner.IsVisible)
        {
            await window.ShowDialog(owner);
        }
        else
        {
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task;
        }

        return accepted;
    }

    public async Task ShowMessageAsync(string title, string message)    {
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
