using CanYeAye.Core;

namespace CanYeAye.Connections.Transports;

/// <summary>
/// Shared plumbing for live transports: connection state, bus list and frame publication.
/// </summary>
public abstract class CanConnectionBase : ICanConnection
{
    private IReadOnlyList<CanBusInfo> _buses;

    protected CanConnectionBase(IReadOnlyList<CanBusInfo> buses, bool listenOnly = false)
    {
        _buses = buses;
        ListenOnly = listenOnly;
    }

    public abstract string Name { get; }

    public abstract ConnectionType Type { get; }

    public ConnectionState State { get; private set; }

    public bool ListenOnly { get; }

    public IReadOnlyList<CanBusInfo> Buses => _buses;

    public event EventHandler<CanFrame>? FrameReceived;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public event EventHandler? BusesChanged;

    public abstract Task ConnectAsync(CancellationToken cancellationToken = default);

    public abstract Task DisconnectAsync(CancellationToken cancellationToken = default);

    public abstract Task SendFrameAsync(int localBus, CanFrame frame, CancellationToken cancellationToken = default);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
        catch
        {
            // Disposing a broken connection is best effort.
        }

        GC.SuppressFinalize(this);
    }

    protected void Publish(CanFrame frame) => FrameReceived?.Invoke(this, frame);

    /// <summary>Throws when this transport is listen-only, so a direct caller cannot transmit.</summary>
    protected void ThrowIfListenOnly()
    {
        if (ListenOnly)
        {
            throw new InvalidOperationException("Connection is listen-only; transmission is disabled.");
        }
    }

    protected void SetBuses(IReadOnlyList<CanBusInfo> buses)
    {
        _buses = buses;
        BusesChanged?.Invoke(this, EventArgs.Empty);
    }

    protected void SetState(ConnectionState state, string? message = null)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state, message));
    }
}
