namespace CanYeAye.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message)
{
    public override string ToString() => $"{Timestamp:HH:mm:ss.fff} [{Level}] {Message}";
}

/// <summary>App-wide log sink; the UI log pane subscribes to <see cref="EntryAdded"/>.</summary>
public sealed class LogBus
{
    private readonly Lock _lock = new();
    private readonly List<LogEntry> _entries = [];

    public event EventHandler<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Debug(string message) => Log(LogLevel.Debug, message);

    public void Info(string message) => Log(LogLevel.Info, message);

    public void Warning(string message) => Log(LogLevel.Warning, message);

    public void Error(string message) => Log(LogLevel.Error, message);

    public void Log(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > 10_000)
            {
                _entries.RemoveRange(0, 2_000);
            }
        }

        EntryAdded?.Invoke(this, entry);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }
}
