using WinCAN.Core;

namespace WinCAN.Connections;

/// <summary>
/// Aggregates live connections and owns the global bus numbering used by the
/// application (the role CanConManager plays in SavvyCAN). Frames arriving from a
/// transport are re-stamped with their global bus number before being published.
/// </summary>
public sealed class CanConnectionManager : IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<ICanConnection, EventHandler<CanFrame>> _frameHandlers = [];
    private readonly Dictionary<ICanConnection, EventHandler> _busHandlers = [];
    private int _nextBus;

    public event EventHandler<CanFrame>? FrameReceived;

    public event EventHandler? ConnectionsChanged;

    public IReadOnlyList<Entry> Connections
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToArray();
            }
        }
    }

    public sealed record Entry(ICanConnection Connection, int FirstBus, int BusCount)
    {
        public int LastBus => FirstBus + BusCount - 1;
    }

    /// <summary>Attaches a transport and gives each of its buses a global bus number.</summary>
    public Entry Attach(ICanConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Entry entry;
        EventHandler<CanFrame> frameHandler;
        EventHandler busHandler;
        lock (_lock)
        {
            var existing = _entries.FirstOrDefault(e => ReferenceEquals(e.Connection, connection));
            if (existing is not null)
            {
                return existing;
            }

            entry = new Entry(connection, _nextBus, Math.Max(1, connection.Buses.Count));
            _nextBus = entry.LastBus + 1;
            _entries.Add(entry);

            frameHandler = new EventHandler<CanFrame>((_, frame) => Publish(connection, frame));
            _frameHandlers[connection] = frameHandler;

            busHandler = new EventHandler((_, _) => OnBusesChanged(connection));
            _busHandlers[connection] = busHandler;
        }

        connection.FrameReceived += frameHandler;
        connection.BusesChanged += busHandler;

        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    public void Detach(ICanConnection connection)
    {
        EventHandler<CanFrame>? frameHandler;
        EventHandler? busHandler;
        lock (_lock)
        {
            _entries.RemoveAll(e => ReferenceEquals(e.Connection, connection));
            _frameHandlers.Remove(connection, out frameHandler);
            _busHandlers.Remove(connection, out busHandler);
        }

        if (frameHandler is not null)
        {
            connection.FrameReceived -= frameHandler;
        }

        if (busHandler is not null)
        {
            connection.BusesChanged -= busHandler;
        }

        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ConnectAsync(ICanConnection connection, CancellationToken cancellationToken = default)
    {
        Attach(connection);
        await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(ICanConnection connection, CancellationToken cancellationToken = default)
    {
        await connection.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        Detach(connection);
    }

    /// <summary>Sends a frame on the global bus number stored in <see cref="CanFrame.Bus"/>.</summary>
    public async Task SendFrameAsync(CanFrame frame, CancellationToken cancellationToken = default)
    {
        Entry? entry;
        lock (_lock)
        {
            entry = _entries.FirstOrDefault(e => frame.Bus >= e.FirstBus && frame.Bus <= e.LastBus);
        }

        if (entry is null)
        {
            throw new InvalidOperationException($"No connection owns bus {frame.Bus}.");
        }

        var localBus = frame.Bus - entry.FirstBus;
        var copy = frame.Clone();
        copy.Bus = (byte)localBus;
        await entry.Connection.SendFrameAsync(localBus, copy, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAllAsync()
    {
        Entry[] entries;
        lock (_lock)
        {
            entries = _entries.ToArray();
            _entries.Clear();
            _frameHandlers.Clear();
            _busHandlers.Clear();
            _nextBus = 0;
        }

        foreach (var entry in entries)
        {
            try
            {
                await entry.Connection.DisconnectAsync().ConfigureAwait(false);
            }
            catch
            {
                // Disconnect is best effort; the connection is being dropped anyway.
            }

            await entry.Connection.DisposeAsync().ConfigureAwait(false);
        }

        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnBusesChanged(ICanConnection connection)
    {
        lock (_lock)
        {
            var next = 0;
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                var count = Math.Max(1, entry.Connection.Buses.Count);
                if (entry.FirstBus != next || entry.BusCount != count)
                {
                    _entries[i] = new Entry(entry.Connection, next, count);
                }

                next += count;
            }

            _nextBus = next;
        }

        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Publish(ICanConnection connection, CanFrame frame)
    {
        Entry? entry;
        lock (_lock)
        {
            entry = _entries.FirstOrDefault(e => ReferenceEquals(e.Connection, connection));
        }

        if (entry is null)
        {
            return;
        }

        var global = frame.Clone();
        global.Bus = (byte)(entry.FirstBus + frame.Bus);
        FrameReceived?.Invoke(this, global);
    }

    public async ValueTask DisposeAsync() => await DisconnectAllAsync().ConfigureAwait(false);
}
