namespace StudioBooking.Contracts;

/// <summary>
/// Messages the Bookings service publishes to the "studio.events" exchange.
/// These records are the contract between services, so they live in their own
/// project and only change in backward-compatible ways (add fields, never rename).
/// </summary>
public static class EventTypes
{
    public const string BookingConfirmed = "booking.confirmed";
    public const string BookingWaitlisted = "booking.waitlisted";
    public const string BookingCancelled = "booking.cancelled";
    public const string WaitlistPromoted = "booking.promoted";
}

public sealed record BookingEvent(
    Guid EventId,
    string EventType,
    Guid BookingId,
    Guid ClassId,
    Guid ClientId,
    string ClassName,
    DateTimeOffset ClassStartsAt,
    int? WaitlistPosition,
    bool LateCancel,
    string? CorrelationId,
    DateTimeOffset OccurredAt);

/// <summary>Shape of a class as the Classes service returns it over HTTP.</summary>
public sealed record ClassDto(
    Guid Id,
    string Name,
    string Instructor,
    DateTimeOffset StartsAt,
    int DurationMinutes,
    int Capacity);
