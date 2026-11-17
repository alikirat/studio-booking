using StudioBooking.Contracts;
using StudioBooking.Domain;

namespace Bookings.Api;

public enum Outcome { Ok, NotFound, Conflict, Unavailable }

public sealed record ServiceResult<T>(Outcome Outcome, T? Value = default, string? Message = null);

public sealed record BookingResponse(
    Guid Id,
    Guid ClassId,
    Guid ClientId,
    string Status,
    int? WaitlistPosition,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CancelledAt,
    DateTimeOffset? PromotedAt,
    bool LateCancel)
{
    public static BookingResponse From(Booking b, int? waitlistPosition = null) => new(
        b.Id, b.ClassId, b.ClientId, b.Status.ToString(), waitlistPosition,
        b.CreatedAt, b.CancelledAt, b.PromotedAt, b.LateCancel);
}

public sealed record CancelResponse(BookingResponse Cancelled, BookingResponse? Promoted);

public sealed record RosterResponse(
    Guid ClassId,
    string ClassName,
    int Capacity,
    int SpotsLeft,
    IReadOnlyList<BookingResponse> Confirmed,
    IReadOnlyList<BookingResponse> Waitlist);

/// <summary>
/// Orchestrates one booking action: ask Classes for the class, apply the rules
/// under the class lock, save, then announce what happened.
/// </summary>
public sealed class BookingService(
    IClassCatalog catalog,
    IBookingStore store,
    IEventPublisher events,
    TimeProvider clock,
    ILogger<BookingService> logger)
{
    public async Task<ServiceResult<BookingResponse>> BookAsync(Guid classId, Guid clientId, string? correlationId, CancellationToken ct)
    {
        var cls = await TryGetClassAsync(classId, ct);
        if (cls.Outcome != Outcome.Ok)
            return new(cls.Outcome, Message: cls.Message);

        var result = await store.WithClassLockAsync(classId, async session =>
        {
            var roster = new ClassRoster(ToSession(cls.Value!), await session.LoadClassAsync(classId, ct));
            var decision = roster.Book(clientId, Guid.NewGuid(), clock.GetUtcNow());
            if (decision.Succeeded)
                await session.InsertAsync(decision.Booking!, ct);
            return decision;
        }, ct);

        if (!result.Succeeded)
        {
            logger.LogInformation("Booking rejected for client {ClientId} in class {ClassId}: {Reason}", clientId, classId, result.Error);
            return new(Outcome.Conflict, Message: Describe(result.Error!.Value));
        }

        var booking = result.Booking!;
        logger.LogInformation("Booking {BookingId} {Status} for client {ClientId} in class {ClassId}",
            booking.Id, booking.Status, clientId, classId);

        var type = booking.Status == BookingStatus.Waitlisted ? EventTypes.BookingWaitlisted : EventTypes.BookingConfirmed;
        await PublishAsync(type, booking, cls.Value!, result.WaitlistPosition, correlationId, ct);

        return new(Outcome.Ok, BookingResponse.From(booking, result.WaitlistPosition));
    }

    public async Task<ServiceResult<CancelResponse>> CancelAsync(Guid bookingId, string? correlationId, CancellationToken ct)
    {
        var existing = await store.GetAsync(bookingId, ct);
        if (existing is null)
            return new(Outcome.NotFound, Message: "Booking not found.");

        var cls = await TryGetClassAsync(existing.ClassId, ct);
        if (cls.Outcome != Outcome.Ok)
            return new(cls.Outcome, Message: cls.Message);

        var result = await store.WithClassLockAsync(existing.ClassId, async session =>
        {
            // Reload inside the lock: the roster may have changed since GetAsync above.
            var roster = new ClassRoster(ToSession(cls.Value!), await session.LoadClassAsync(existing.ClassId, ct));
            var decision = roster.Cancel(bookingId, clock.GetUtcNow());
            if (decision.Succeeded)
            {
                await session.UpdateAsync(decision.Cancelled!, ct);
                if (decision.Promoted is not null)
                    await session.UpdateAsync(decision.Promoted, ct);
            }
            return decision;
        }, ct);

        if (!result.Succeeded)
            return new(result.Error == BookingError.BookingNotFound ? Outcome.NotFound : Outcome.Conflict,
                Message: Describe(result.Error!.Value));

        logger.LogInformation("Booking {BookingId} cancelled (late: {LateCancel})", bookingId, result.Cancelled!.LateCancel);
        await PublishAsync(EventTypes.BookingCancelled, result.Cancelled, cls.Value!, null, correlationId, ct);

        if (result.Promoted is not null)
        {
            logger.LogInformation("Booking {BookingId} promoted from waitlist for client {ClientId}",
                result.Promoted.Id, result.Promoted.ClientId);
            await PublishAsync(EventTypes.WaitlistPromoted, result.Promoted, cls.Value!, null, correlationId, ct);
        }

        return new(Outcome.Ok, new CancelResponse(
            BookingResponse.From(result.Cancelled),
            result.Promoted is null ? null : BookingResponse.From(result.Promoted)));
    }

    public async Task<ServiceResult<RosterResponse>> GetRosterAsync(Guid classId, CancellationToken ct)
    {
        var cls = await TryGetClassAsync(classId, ct);
        if (cls.Outcome != Outcome.Ok)
            return new(cls.Outcome, Message: cls.Message);

        var bookings = await store.ListForClassAsync(classId, ct);
        var roster = new ClassRoster(ToSession(cls.Value!), bookings);
        var confirmed = bookings
            .Where(b => b.Status == BookingStatus.Confirmed)
            .Select(b => BookingResponse.From(b))
            .ToList();
        var waitlist = roster.Waitlist.Select((b, i) => BookingResponse.From(b, i + 1)).ToList();

        return new(Outcome.Ok, new RosterResponse(classId, cls.Value!.Name, cls.Value.Capacity, roster.SpotsLeft, confirmed, waitlist));
    }

    private async Task<ServiceResult<ClassDto>> TryGetClassAsync(Guid classId, CancellationToken ct)
    {
        try
        {
            var cls = await catalog.GetClassAsync(classId, ct);
            return cls is null
                ? new(Outcome.NotFound, Message: "Class not found.")
                : new(Outcome.Ok, cls);
        }
        catch (ClassCatalogUnavailableException ex)
        {
            return new(Outcome.Unavailable, Message: ex.Message);
        }
    }

    /// <summary>
    /// The booking is already saved, so a messaging failure must not turn into an
    /// error for the client. It is logged loudly instead. The robust fix is a
    /// transactional outbox (see README, "What I'd do next").
    /// </summary>
    private async Task PublishAsync(string type, Booking b, ClassDto cls, int? position, string? correlationId, CancellationToken ct)
    {
        var evt = new BookingEvent(Guid.NewGuid(), type, b.Id, b.ClassId, b.ClientId, cls.Name, cls.StartsAt,
            position, b.LateCancel, correlationId, clock.GetUtcNow());
        try
        {
            await events.PublishAsync(evt, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to publish {EventType} for booking {BookingId}", type, b.Id);
        }
    }

    private static ClassSession ToSession(ClassDto cls) => new(cls.Id, cls.StartsAt, cls.Capacity);

    private static string Describe(BookingError error) => error switch
    {
        BookingError.ClassAlreadyStarted => "This class has already started.",
        BookingError.AlreadyBooked => "This client already has a booking or waitlist spot for this class.",
        BookingError.AlreadyCancelled => "This booking is already cancelled.",
        BookingError.BookingNotFound => "Booking not found.",
        _ => error.ToString()
    };
}
