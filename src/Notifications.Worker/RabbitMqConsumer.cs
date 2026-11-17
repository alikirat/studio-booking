using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StudioBooking.Contracts;

namespace Notifications.Worker;

/// <summary>
/// Background consumer for the "notifications" queue.
///
/// Topology (declared here so the service can start against an empty broker):
///   studio.events (topic)  --booking.*-->  notifications  --rejected-->  studio.events.dlx  -->  notifications.dead
///
/// Messages are acked only after they are handled. A message that can't be parsed or
/// handled is rejected without requeue, which sends it to the dead-letter queue for
/// inspection instead of retrying forever and blocking the queue.
/// </summary>
public sealed class RabbitMqConsumer(
    IConfiguration config,
    NotificationHandler handler,
    ILogger<RabbitMqConsumer> logger) : BackgroundService
{
    private const string Exchange = "studio.events";
    private const string Queue = "notifications";
    private const string DeadLetterExchange = "studio.events.dlx";
    private const string DeadLetterQueue = "notifications.dead";

    private IConnection? _connection;
    private IModel? _channel;

    /// <summary>True once the queue is declared and bound, so no published event can be dropped.</summary>
    public bool IsListening => _channel is { IsOpen: true } && _listening;
    private volatile bool _listening;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = config["RabbitMq:Host"] ?? "localhost",
            UserName = config["RabbitMq:User"] ?? "guest",
            Password = config["RabbitMq:Password"] ?? "guest",
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = true,
            ClientProvidedName = "notifications-worker"
        };

        // RabbitMQ can take a while to boot; keep trying instead of crashing.
        for (var attempt = 1; _connection is null && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                _connection = factory.CreateConnection();
            }
            catch (Exception ex)
            {
                logger.LogWarning("RabbitMQ not reachable (attempt {Attempt}): {Error}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, attempt * 2)), stoppingToken);
            }
        }

        if (_connection is null)
            return;

        _channel = _connection.CreateModel();
        _channel.ExchangeDeclare(Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        _channel.ExchangeDeclare(DeadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false);
        _channel.QueueDeclare(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false);
        _channel.QueueBind(DeadLetterQueue, DeadLetterExchange, routingKey: "");

        _channel.QueueDeclare(Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object> { ["x-dead-letter-exchange"] = DeadLetterExchange });
        _channel.QueueBind(Queue, Exchange, routingKey: "booking.*");

        // Don't let one consumer hoard messages it hasn't processed yet.
        _channel.BasicQos(prefetchSize: 0, prefetchCount: 10, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += OnMessageAsync;
        _channel.BasicConsume(Queue, autoAck: false, consumer: consumer);

        _listening = true;
        logger.LogInformation("Listening on queue {Queue}", Queue);
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
    }

    private Task OnMessageAsync(object sender, BasicDeliverEventArgs ea)
    {
        try
        {
            var evt = JsonSerializer.Deserialize<BookingEvent>(ea.Body.Span)
                      ?? throw new JsonException("Empty message body");
            handler.Handle(evt);
            _channel!.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not process message {MessageId}; sending to dead-letter queue", ea.BasicProperties?.MessageId);
            _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
        }

        return Task.CompletedTask;
    }

    public sealed class HealthCheck(RabbitMqConsumer consumer) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken ct = default) =>
            Task.FromResult(consumer.IsListening
                ? HealthCheckResult.Healthy("Consuming from RabbitMQ")
                : HealthCheckResult.Unhealthy("Not connected to RabbitMQ yet"));
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
