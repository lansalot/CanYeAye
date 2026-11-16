namespace CanYeAye.Connections;

using CanYeAye.Core;

public enum ConnectionType
{
    GvretSerial,
    LawicelSerial,
    SocketCanD,
    CanServer,
    CanLogServer,
    Mqtt,
    Pcan,
    VectorXl,
}

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Error,
}

public sealed record CanBusInfo(int LocalIndex, string Name);

public sealed record ConnectionStateChangedEventArgs(ConnectionState State, string? Message = null);

/// <summary>
/// A live CAN transport. Implementations expose one or more local buses; the
/// <see cref="CanConnectionManager"/> maps them to global bus numbers, matching
/// SavvyCAN's connection/bus model.
/// </summary>
public interface ICanConnection : IAsyncDisposable
{
    string Name { get; }

    ConnectionType Type { get; }

    ConnectionState State { get; }

    IReadOnlyList<CanBusInfo> Buses { get; }

    event EventHandler<CanFrame>? FrameReceived;

    event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>Raised when a transport discovers a different number/name of buses.</summary>
    event EventHandler? BusesChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends a frame on a local bus index (see <see cref="Buses"/>).</summary>
    Task SendFrameAsync(int localBus, CanFrame frame, CancellationToken cancellationToken = default);
}

/// <summary>Serial parameters shared by GVRET and Lawicel adapters.</summary>
public sealed record SerialConnectionOptions(string PortName, int BaudRate = 115200);

public sealed record SocketCandOptions(string Host, int Port = 29536, string Interface = "can0");

public sealed record MqttOptions(
    string Host,
    int Port = 1883,
    string? Username = null,
    string? Password = null,
    string BaseTopic = "canyeaye",
    bool UseTls = false);

public sealed record PcanOptions(string Channel, int Bitrate = 500_000, bool Fd = false);

public sealed record VectorXlOptions(int Channel = 0, int Bitrate = 500_000);
