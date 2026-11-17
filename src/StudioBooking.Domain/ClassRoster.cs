namespace StudioBooking.Domain;

/// <summary>The facts about a class that the booking rules need.</summary>
public sealed record ClassSession(Guid Id, DateTimeOffset StartsAt, int Capacity);

public enum BookingError
{
    ClassAlreadyStarted,
    AlreadyBooked,
    BookingNotFound,
    AlreadyCancelled
}

public sealed record BookResult(Booking? Booking, int? WaitlistPosition, BookingError? Error)
{
    public bool Succeeded => Error is null;
}

public sealed record CancelResult(Booking? Cancelled, Booking? Promoted, BookingError? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// The booking rules for a single class, with no I/O.
///
/// The caller loads the class and its bookings, asks the roster for a decision,
/// then saves whatever changed. Keeping the rules pure means they are fully
/// covered by fast unit tests, and the database code stays simple.
/// </summary>
public sealed class ClassRoster
{
    /// <summary>Cancelling inside this window is flagged as a late cancel (studios often charge a fee).</summary>
    public static readonly TimeSpan LateCancelWindow = TimeSpan.FromHours(12);

    private readonly ClassSession _session;
    private readonly List<Booking> _bookings;

    public ClassRoster(ClassSession session, IEnumerable<Booking> bookings)
    {
        if (session.Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(session), "Capacity must be positive.");

        _session = session;
        _bookings = bookings.Where(b => b.ClassId == session.Id).ToList();
    }

    public int ConfirmedCount => _bookings.Count(b => b.Status == BookingStatus.Confirmed);
    public int SpotsLeft => Math.Max(0, _session.Capacity - ConfirmedCount);

    /// <summary>Waitlisted bookings in the order they will be promoted (first come, first served).</summary>
    public IReadOnlyList<Booking> Waitlist => _bookings
        .Where(b => b.Status == BookingStatus.Waitlisted)
        .OrderBy(b => b.CreatedAt)
        .ThenBy(b => b.Id)
        .ToList();

    public BookResult Book(Guid clientId, Guid bookingId, DateTimeOffset now)
    {
        if (now >= _session.StartsAt)
            return new BookResult(null, null, BookingError.ClassAlreadyStarted);

        if (_bookings.Any(b => b.ClientId == clientId && b.IsActive))
            return new BookResult(null, null, BookingError.AlreadyBooked);

        var status = ConfirmedCount < _session.Capacity
            ? BookingStatus.Confirmed
            : BookingStatus.Waitlisted;

        var booking = new Booking(bookingId, _session.Id, clientId, status, now);
        _bookings.Add(booking);

        int? position = status == BookingStatus.Waitlisted ? Waitlist.Count : null;
        return new BookResult(booking, position, null);
    }

    public CancelResult Cancel(Guid bookingId, DateTimeOffset now)
    {
        var booking = _bookings.FirstOrDefault(b => b.Id == bookingId);
        if (booking is null)
            return new CancelResult(null, null, BookingError.BookingNotFound);
        if (!booking.IsActive)
            return new CancelResult(null, null, BookingError.AlreadyCancelled);

        var freedASpot = booking.Status == BookingStatus.Confirmed;
        var late = _session.StartsAt - now < LateCancelWindow;
        booking.Cancel(now, late);

        // Only promote while the class hasn't started; nobody wants a
        // "you're in!" message for a class that is already underway.
        Booking? promoted = null;
        if (freedASpot && now < _session.StartsAt && ConfirmedCount < _session.Capacity)
        {
            promoted = Waitlist.FirstOrDefault();
            promoted?.Promote(now);
        }

        return new CancelResult(booking, promoted, null);
    }
}
