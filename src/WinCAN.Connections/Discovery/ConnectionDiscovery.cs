using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace WinCAN.Connections.Discovery;

public enum DiscoveredConnectionType
{
    Gvret,
    SocketCand,
}

public sealed record DiscoveredDevice(
    DiscoveredConnectionType Type,
    string Address,
    string Description,
    string? PortString = null,
    IReadOnlyList<string>? Buses = null,
    int Port = 0);

/// <summary>
/// Passive UDP discovery (connections/connectionwindow.cpp): GVRET devices broadcast to port
/// 17222, socketcand/Kayak sends XML CANBeacon datagrams to port 42000. Any GVRET datagram just
/// identifies the sender; Kayak beacons carry the bus list and URL.
/// </summary>
public sealed class ConnectionDiscovery : IDisposable
{
    public const int GvretPort = 17222;
    public const int SocketCandPort = 42000;

    private UdpClient? _gvret;
    private UdpClient? _socketCand;
    private CancellationTokenSource? _cts;
    private Task? _gvretLoop;
    private Task? _socketCandLoop;

    public event EventHandler<DiscoveredDevice>? DeviceFound;

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _gvret = TryBind(GvretPort);
        _socketCand = TryBind(SocketCandPort);

        if (_gvret is not null)
        {
            _gvretLoop = Task.Run(() => ReceiveGvretAsync(_gvret, _cts.Token));
        }

        if (_socketCand is not null)
        {
            _socketCandLoop = Task.Run(() => ReceiveSocketCandAsync(_socketCand, _cts.Token));
        }
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        _gvret?.Dispose();
        _socketCand?.Dispose();
        _gvret = null;
        _socketCand = null;
        _gvretLoop = null;
        _socketCandLoop = null;
        cts?.Dispose();
    }

    public void Dispose() => Stop();

    /// <summary>Builds the GVRET entry for a datagram sender (the payload is not inspected).</summary>
    public static DiscoveredDevice DescribeGvretSender(string address) =>
        new(DiscoveredConnectionType.Gvret, address, $"GVRET device at {address}");

    /// <summary>Parses a socketcand CANBeacon XML document into a device.</summary>
    public static DiscoveredDevice? ParseKayakBeacon(string xml, string senderAddress)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var beacon = document.Root;
        if (beacon is null || beacon.Name.LocalName != "CANBeacon")
        {
            return null;
        }

        var name = beacon.Attribute("name")?.Value ?? string.Empty;
        var url = beacon.Elements().FirstOrDefault(e => e.Name.LocalName == "URL")?.Value ?? string.Empty;
        var buses = beacon.Elements()
            .Where(e => e.Name.LocalName == "Bus")
            .Select(e => e.Attribute("name")?.Value)
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)
            .ToArray();

        if (buses.Length == 0)
        {
            return null;
        }

        var portString = string.Join(',', buses) + "@" + name + " (" + url + ")";
        var (host, port) = ParseUrl(url, senderAddress);
        return new DiscoveredDevice(
            DiscoveredConnectionType.SocketCand,
            host,
            $"{name} ({host}:{port}) buses {string.Join(", ", buses)}",
            portString,
            buses,
            port);
    }

    private static (string Host, int Port) ParseUrl(string url, string fallback)
    {
        var index = url.IndexOf("can://", StringComparison.OrdinalIgnoreCase);
        var tail = index >= 0 ? url[(index + 6)..] : url;
        var colon = tail.LastIndexOf(':');
        if (colon > 0 && int.TryParse(tail[(colon + 1)..].TrimEnd(')', ' '), out var port))
        {
            return (tail[..colon].Trim(), port);
        }

        return (fallback, 29536);
    }

    private static UdpClient? TryBind(int port)
    {
        try
        {
            var client = new UdpClient();
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            return client;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private async Task ReceiveGvretAsync(UdpClient client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                DeviceFound?.Invoke(this, DescribeGvretSender(result.RemoteEndPoint.Address.ToString()));
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (ObjectDisposedException)
        {
            // normal shutdown
        }
        catch (SocketException)
        {
            // listener went away
        }
    }

    private async Task ReceiveSocketCandAsync(UdpClient client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(result.Buffer);
                var device = ParseKayakBeacon(text, result.RemoteEndPoint.Address.ToString());
                if (device is not null)
                {
                    DeviceFound?.Invoke(this, device);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (ObjectDisposedException)
        {
            // normal shutdown
        }
        catch (SocketException)
        {
            // listener went away
        }
    }
}
