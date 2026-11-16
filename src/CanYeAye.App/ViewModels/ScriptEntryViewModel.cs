using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CanYeAye.Core;
using CanYeAye.Core.Scripting;

namespace CanYeAye.App.ViewModels;

public partial class ScriptParameterRow : ObservableObject
{
    public ScriptParameterRow(string name) => Name = name;

    public string Name { get; }

    [ObservableProperty]
    private string _value = string.Empty;
}

/// <summary>One loaded script: its Jint host, editor text, tick timer and log.</summary>
public partial class ScriptEntryViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _tickTimer;
    private readonly DateTime _started = DateTime.Now;

    public ScriptEntryViewModel(string name, string text, string? filePath, Action<CanFrame> sendFrame)
    {
        Name = name;
        FilePath = filePath;
        EditorText = text;

        Host = new ScriptHost
        {
            FileName = name,
            FilePath = filePath ?? string.Empty,
        };

        Host.LogEmitted += (_, message) => Logs.Add($"{DateTime.Now - _started:mm\\:ss\\.fff} ({name}): {message}");
        Host.FrameToSend += (_, frame) => sendFrame(frame);
        Host.TickIntervalChanged += (_, interval) => SetTickInterval(interval);

        _tickTimer = new DispatcherTimer();
        _tickTimer.Tick += (_, _) => Host.Tick();
    }

    public string Name { get; private set; }

    public string? FilePath { get; set; }

    public ScriptHost Host { get; }

    [ObservableProperty]
    private string _editorText;

    public ObservableCollection<string> Logs { get; } = [];

    public ObservableCollection<ScriptParameterRow> Parameters { get; } = [];

    public bool Compile() => Host.Compile(EditorText, Name);

    public void RefreshParameters()
    {
        foreach (var name in Host.Parameters)
        {
            var row = Parameters.FirstOrDefault(p => p.Name == name);
            if (row is null)
            {
                row = new ScriptParameterRow(name);
                Parameters.Add(row);
            }

            row.Value = Host.GetParameterValue(name);
        }
    }

    public void Rename(string name)
    {
        Name = name;
        Host.FileName = name;
    }

    public void Dispose()
    {
        _tickTimer.Stop();
        Host.Stop();
    }

    private void SetTickInterval(int milliseconds)
    {
        if (milliseconds > 0)
        {
            _tickTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
            _tickTimer.Start();
        }
        else
        {
            _tickTimer.Stop();
        }
    }
}
