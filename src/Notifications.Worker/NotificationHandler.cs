using System.Collections.Concurrent;
using StudioBooking.Contracts;
using StudioBooking.ServiceDefaults;

namespace Notifications.Worker;

public sealed record SentNotification(Guid EventId, Guid ClientId, string Channel, string Message, DateTimeOffset SentAt);

/// <summary>
/// Turns booking events into client messages. Sending is simulated (logged), since
/// the point of the project is the event flow, not an email provider.
///
/// RabbitMQ guarantees at-least-once delivery, so the same event can arrive twice
/// (for example after a consumer crash before the ack). The handler remembers recent
/// event IDs and skips repeats, so a client never gets the same message twice.
/// </summary>
public sealed class NotificationHandler(ILogger<NotificationHandler> logger, TimeProvider clock)
{
    private const int MaxRemembered = 500;

    private readonly ConcurrentDictionary<Guid, byte> _seen = new();
    private readonly ConcurrentQueue<Guid> _seenOrder = new();
    private readonly ConcurrentQueue<SentNotification> _recent = new();

    public IReadOnlyList<SentNotification> Recent => _recent.Reverse().ToList();

    /// <returns>False if the event was a duplicate and was skipped.</returns>
    public bool Handle(BookingEvent evt)
    {
        if (!_seen.TryAdd(evt.EventId, 0))
        {
            logger.LogInformation("Skipping duplicate event {EventId} ({EventType})", evt.EventId, evt.EventType);
            return false;
        }

        Remember(evt.EventId);

        using var scope = logger.BeginScope(new LogScope(("CorrelationId", evt.CorrelationId), ("EventType", evt.EventType)));

        var message = Compose(evt);
        if (message is null)
        {
            logger.LogWarning("No template for event type {EventType}; ignoring", evt.EventType);
            return true;
        }

        var sent = new SentNotification(evt.EventId, evt.ClientId, "email", message, clock.GetUtcNow());
        _recent.Enqueue(sent);
        while (_recent.Count > 50 && _recent.TryDequeue(out _)) { }

        logger.LogInformation("Notified client {ClientId} about booking {BookingId} ({EventType}): {Message}",
            evt.ClientId, evt.BookingId, evt.EventType, message);
        return true;
    }

    private static string? Compose(BookingEvent e)
    {
        var when = e.ClassStartsAt.ToString("ddd MMM d 'at' HH:mm 'UTC'");
        return e.EventType switch
        {
            EventTypes.BookingConfirmed => $"You're booked for {e.ClassName} on {when}. See you there!",
            EventTypes.BookingWaitlisted => $"{e.ClassName} on {when} is full. You're #{e.WaitlistPosition} on the waitlist, and we'll let you know if a spot opens.",
            EventTypes.WaitlistPromoted => $"Good news: a spot opened in {e.ClassName} on {when}, and it's yours.",
            EventTypes.BookingCancelled when e.LateCancel => $"Your booking for {e.ClassName} on {when} is cancelled. This was inside the 12-hour window, so a late-cancel fee may apply.",
            EventTypes.BookingCancelled => $"Your booking for {e.ClassName} on {when} is cancelled.",
            _ => null
        };
    }

    private void Remember(Guid eventId)
    {
        _seenOrder.Enqueue(eventId);
        while (_seenOrder.Count > MaxRemembered && _seenOrder.TryDequeue(out var old))
            _seen.TryRemove(old, out _);
    }
}
