using System.Buffers;
using MQTTnet;
using MQTTnet.Protocol;
using CanYeAye.Connections.Protocols;
using CanYeAye.Core;

namespace CanYeAye.Connections.Transports;

public sealed record MqttConnectionOptions
{
    public required string Host { get; init; }

    public int Port { get; init; } = 1883;

    public string? Username { get; init; }

    public string? Password { get; init; }

    /// <summary>Topic base; frames are published to <c>&lt;base&gt;/s/&lt;id&gt;</c>.</summary>
    public string BaseTopic { get; init; } = "canyeaye";

    public bool UseTls { get; init; }

    public bool UseSystemTime { get; init; }
}

/// <summary>
/// MQTT bus (connections/mqtt_bus.cpp) via MQTTnet. Port 8883 implies TLS (the reference default
/// for api.savvycan.com); CanYeAye makes TLS explicit. One bus, framed by <see cref="MqttFrameCodec"/>.
/// </summary>
public sealed class MqttConnection : CanConnectionBase
{
    private readonly MqttConnectionOptions _options;
    private readonly IMqttClient _client;
    private bool _useSystemTime;

    public MqttConnection(MqttConnectionOptions options)
        : base([new CanBusInfo(0, "CAN0")])
    {
        _options = options;
        _useSystemTime = options.UseSystemTime;
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
    }

    public override string Name => $"MQTT {_options.Host}:{_options.Port}/{_options.BaseTopic}";

    public override ConnectionType Type => ConnectionType.Mqtt;

    public bool UseSystemTime
    {
        get => _useSystemTime;
        set => _useSystemTime = value;
    }

    public override async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (State is ConnectionState.Connected or ConnectionState.Connecting)
        {
            return;
        }

        SetState(ConnectionState.Connecting);
        try
        {
            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(_options.Host, _options.Port)
                .WithClientId(MqttFrameCodec.CreateClientId())
                .WithCleanSession();

            if (_options.UseTls)
            {
                builder.WithTlsOptions(tls => tls.UseTls());
            }

            if (!string.IsNullOrEmpty(_options.Username))
            {
                builder.WithCredentials(_options.Username, _options.Password ?? string.Empty);
            }

            await _client.ConnectAsync(builder.Build(), cancellationToken).ConfigureAwait(false);

            var subscribe = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(
                    MqttFrameCodec.SubscriptionTopic(_options.BaseTopic),
                    MqttQualityOfServiceLevel.AtMostOnce)
                .Build();
            await _client.SubscribeAsync(subscribe, cancellationToken).ConfigureAwait(false);

            SetState(ConnectionState.Connected);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetState(ConnectionState.Error, ex.Message);
            throw;
        }
    }

    public override async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(ConnectionState.Disconnecting);
        try
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync(
                    new MqttClientDisconnectOptionsBuilder().Build(),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // best effort
        }

        SetState(ConnectionState.Disconnected);
    }

    public override async Task SendFrameAsync(int localBus, CanFrame frame, CancellationToken cancellationToken = default)
    {
        if (State != ConnectionState.Connected)
        {
            throw new InvalidOperationException("MQTT connection is not connected.");
        }

        var topic = MqttFrameCodec.FrameTopic(_options.BaseTopic, frame.FrameId);
        var payload = MqttFrameCodec.Encode(frame, GvretProtocol.NowMicroseconds());
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .Build();

        await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var message = args.ApplicationMessage;
        var id = MqttFrameCodec.ParseTopicFrameId(message.Topic);
        if (id is null)
        {
            return Task.CompletedTask;
        }

        var payload = message.Payload.ToArray();
        var frame = MqttFrameCodec.Decode(id.Value, payload, _useSystemTime, GvretProtocol.NowMicroseconds());
        if (frame is not null)
        {
            Publish(frame);
        }

        return Task.CompletedTask;
    }
}
