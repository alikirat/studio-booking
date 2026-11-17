namespace StudioBooking.Domain;

public enum BookingStatus
{
    Confirmed,
    Waitlisted,
    Cancelled
}

/// <summary>
/// A client's spot (or place in line) for one class session.
/// State changes only happen through <see cref="ClassRoster"/>, so the
/// capacity and waitlist rules can't be bypassed by setting a property.
/// </summary>
public sealed class Booking
{
    public Guid Id { get; }
    public Guid ClassId { get; }
    public Guid ClientId { get; }
    public BookingStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public DateTimeOffset? PromotedAt { get; private set; }
    public bool LateCancel { get; private set; }

    public Booking(
        Guid id,
        Guid classId,
        Guid clientId,
        BookingStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset? cancelledAt = null,
        DateTimeOffset? promotedAt = null,
        bool lateCancel = false)
    {
        Id = id;
        ClassId = classId;
        ClientId = clientId;
        Status = status;
        CreatedAt = createdAt;
        CancelledAt = cancelledAt;
        PromotedAt = promotedAt;
        LateCancel = lateCancel;
    }

    public bool IsActive => Status != BookingStatus.Cancelled;

    internal void Cancel(DateTimeOffset now, bool late)
    {
        Status = BookingStatus.Cancelled;
        CancelledAt = now;
        LateCancel = late;
    }

    internal void Promote(DateTimeOffset now)
    {
        Status = BookingStatus.Confirmed;
        PromotedAt = now;
    }
}
