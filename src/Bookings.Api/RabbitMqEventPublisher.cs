using System.Text.Json;
using RabbitMQ.Client;
using StudioBooking.Contracts;

namespace Bookings.Api;

/// <summary>
/// Publishes booking events to a durable topic exchange. Routing keys are the
/// event types ("booking.confirmed", "booking.promoted", ...), so any future service
/// (billing, analytics) can bind its own queue to just the events it cares about
/// without the Bookings service knowing it exists.
/// </summary>
public sealed class RabbitMqEventPublisher : IEventPublisher, IDisposable
{
    public const string Exchange = "studio.events";

    private readonly ConnectionFactory _factory;
    private readonly ILogger<RabbitMqEventPublisher> _logger;
    private readonly object _gate = new();
    private IConnection? _connection;
    private IModel? _channel;

    public RabbitMqEventPublisher(IConfiguration config, ILogger<RabbitMqEventPublisher> logger)
    {
        _logger = logger;
        _factory = new ConnectionFactory
        {
            HostName = config["RabbitMq:Host"] ?? "localhost",
            UserName = config["RabbitMq:User"] ?? "guest",
            Password = config["RabbitMq:Password"] ?? "guest",
            AutomaticRecoveryEnabled = true,
            ClientProvidedName = "bookings-api"
        };
    }

    public Task PublishAsync(BookingEvent evt, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(evt);

        // A channel is not thread-safe, and requests publish concurrently.
        lock (_gate)
        {
            var channel = EnsureChannel();
            var props = channel.CreateBasicProperties();
            props.Persistent = true;                 // survive a broker restart
            props.ContentType = "application/json";
            props.MessageId = evt.EventId.ToString();
            props.Type = evt.EventType;
            props.CorrelationId = evt.CorrelationId;

            channel.BasicPublish(Exchange, routingKey: evt.EventType, mandatory: false, basicProperties: props, body: body);
        }

        _logger.LogInformation("Published {EventType} for booking {BookingId}", evt.EventType, evt.BookingId);
        return Task.CompletedTask;
    }

    private IModel EnsureChannel()
    {
        if (_channel is { IsOpen: true })
            return _channel;

        _connection ??= _factory.CreateConnection();
        _channel = _connection.CreateModel();
        _channel.ExchangeDeclare(Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        return _channel;
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
    }
}
