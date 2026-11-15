using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCAN.App.Services;
using WinCAN.Connections;
using WinCAN.Core;

namespace WinCAN.App.ViewModels;

/// <summary>
/// Scripting window state: a list of unmodified SavvyCAN scripts, each on its own Jint host.
/// Frames from the connection manager are dispatched to every running script on the UI thread.
/// </summary>
public partial class ScriptingWindowViewModel : ObservableObject, IDisposable
{
    private const string Template = """
        function setup ()
        {
            host.log("Hello from WinCAN");
        }

        function tick ()
        {
        }
        """;

    private readonly CanConnectionManager _manager;
    private readonly IFileDialogService _dialogs;
    private readonly DispatcherTimer _parameterTimer;
    private int _unnamedCounter = 1;

    public ScriptingWindowViewModel(CanConnectionManager manager, IFileDialogService dialogs)
    {
        _manager = manager;
        _dialogs = dialogs;

        _manager.FrameReceived += OnFrameReceived;

        _parameterTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _parameterTimer.Tick += (_, _) => SelectedScript?.RefreshParameters();
        _parameterTimer.Start();
    }

    public ObservableCollection<ScriptEntryViewModel> Scripts { get; } = [];

    [ObservableProperty]
    private ScriptEntryViewModel? _selectedScript;

    partial void OnSelectedScriptChanged(ScriptEntryViewModel? value)
    {
        value?.RefreshParameters();
    }

    [RelayCommand]
    private async Task LoadScriptAsync()
    {
        var path = await _dialogs.PickOpenScriptAsync();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path);
            var entry = new ScriptEntryViewModel(Path.GetFileName(path), text, path, SendFrame);
            Scripts.Add(entry);
            SelectedScript = entry;
            entry.Compile();
            entry.RefreshParameters();
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Load script failed", ex.Message);
        }
    }

    [RelayCommand]
    private void NewScript()
    {
        var entry = new ScriptEntryViewModel($"UNNAMED_{_unnamedCounter++}.js", Template, null, SendFrame);
        Scripts.Add(entry);
        SelectedScript = entry;
    }

    [RelayCommand]
    private void DeleteScript()
    {
        var entry = SelectedScript;
        if (entry is null)
        {
            return;
        }

        var index = Scripts.IndexOf(entry);
        entry.Dispose();
        Scripts.Remove(entry);
        SelectedScript = Scripts.Count > 0 ? Scripts[Math.Clamp(index, 0, Scripts.Count - 1)] : null;
    }

    [RelayCommand]
    private void Recompile()
    {
        var entry = SelectedScript;
        if (entry is null)
        {
            return;
        }

        entry.Host.FileName = entry.Name;
        entry.Compile();
        entry.RefreshParameters();
    }

    [RelayCommand]
    private async Task SaveScriptAsync()
    {
        var entry = SelectedScript;
        if (entry is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(entry.FilePath))
        {
            await SaveScriptAsAsync();
            return;
        }

        try
        {
            await File.WriteAllTextAsync(entry.FilePath, entry.EditorText);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Save script failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task SaveScriptAsAsync()
    {
        var entry = SelectedScript;
        if (entry is null)
        {
            return;
        }

        var path = await _dialogs.PickSaveScriptAsync(entry.Name);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, entry.EditorText);
            entry.FilePath = path;
            entry.Rename(Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Save script failed", ex.Message);
        }
    }

    [RelayCommand]
    private void ClearLog() => SelectedScript?.Logs.Clear();

    public void Dispose()
    {
        _manager.FrameReceived -= OnFrameReceived;
        _parameterTimer.Stop();
        foreach (var script in Scripts)
        {
            script.Dispose();
        }
    }

    private void OnFrameReceived(object? sender, CanFrame frame)
    {
        if (Scripts.Count == 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var script in Scripts)
            {
                try
                {
                    script.Host.DispatchFrame(frame);
                }
                catch (Exception ex)
                {
                    script.Logs.Add($"Dispatch error: {ex.Message}");
                }
            }
        });
    }

    private async void SendFrame(CanFrame frame)
    {
        try
        {
            await _manager.SendFrameAsync(frame);
        }
        catch (Exception ex)
        {
            foreach (var script in Scripts)
            {
                script.Logs.Add($"TX error: {ex.Message}");
            }
        }
    }
}
