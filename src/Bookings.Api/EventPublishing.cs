using StudioBooking.Contracts;

namespace Bookings.Api;

public interface IEventPublisher
{
    Task PublishAsync(BookingEvent evt, CancellationToken ct);
}

/// <summary>
/// For running without RabbitMQ: posts events straight to the Notifications
/// service. Handy for local development, but it couples the two services in time
/// (if Notifications is down, the event is lost), which is exactly what the queue avoids.
/// </summary>
public sealed class HttpEventPublisher(HttpClient http) : IEventPublisher
{
    public async Task PublishAsync(BookingEvent evt, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/dev/events", evt, ct);
        response.EnsureSuccessStatusCode();
    }
}

public sealed class NoOpEventPublisher(ILogger<NoOpEventPublisher> logger) : IEventPublisher
{
    public Task PublishAsync(BookingEvent evt, CancellationToken ct)
    {
        logger.LogDebug("Messaging disabled; dropped {EventType} for booking {BookingId}", evt.EventType, evt.BookingId);
        return Task.CompletedTask;
    }
}
