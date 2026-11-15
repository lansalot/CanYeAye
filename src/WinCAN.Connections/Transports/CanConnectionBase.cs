using WinCAN.Core;

namespace WinCAN.Connections.Transports;

/// <summary>
/// Shared plumbing for live transports: connection state, bus list and frame publication.
/// </summary>
public abstract class CanConnectionBase : ICanConnection
{
    private IReadOnlyList<CanBusInfo> _buses;

    protected CanConnectionBase(IReadOnlyList<CanBusInfo> buses)
    {
        _buses = buses;
    }

    public abstract string Name { get; }

    public abstract ConnectionType Type { get; }

    public ConnectionState State { get; private set; }

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
