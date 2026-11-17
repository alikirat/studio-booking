using StudioBooking.Domain;

namespace StudioBooking.Domain.Tests;

public class ClassRosterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid ClassId = Guid.NewGuid();

    private static ClassSession Session(int capacity = 2, TimeSpan? startsIn = null) =>
        new(ClassId, Now + (startsIn ?? TimeSpan.FromDays(1)), capacity);

    private static ClassRoster Roster(int capacity = 2, TimeSpan? startsIn = null) =>
        new(Session(capacity, startsIn), Array.Empty<Booking>());

    // ---------- booking ----------

    [Fact]
    public void Book_WithOpenSpots_IsConfirmed()
    {
        var roster = Roster(capacity: 2);

        var result = roster.Book(Guid.NewGuid(), Guid.NewGuid(), Now);

        Assert.True(result.Succeeded);
        Assert.Equal(BookingStatus.Confirmed, result.Booking!.Status);
        Assert.Null(result.WaitlistPosition);
        Assert.Equal(1, roster.SpotsLeft);
    }

    [Fact]
    public void Book_WhenFull_IsWaitlistedWithPosition()
    {
        var roster = Roster(capacity: 1);
        roster.Book(Guid.NewGuid(), Guid.NewGuid(), Now);

        var second = roster.Book(Guid.NewGuid(), Guid.NewGuid(), Now.AddMinutes(1));
        var third = roster.Book(Guid.NewGuid(), Guid.NewGuid(), Now.AddMinutes(2));

        Assert.Equal(BookingStatus.Waitlisted, second.Booking!.Status);
        Assert.Equal(1, second.WaitlistPosition);
        Assert.Equal(2, third.WaitlistPosition);
        Assert.Equal(1, roster.ConfirmedCount);
    }

    [Fact]
    public void Book_SameClientTwice_IsRejected()
    {
        var roster = Roster();
        var client = Guid.NewGuid();
        roster.Book(client, Guid.NewGuid(), Now);

        var again = roster.Book(client, Guid.NewGuid(), Now);

        Assert.False(again.Succeeded);
        Assert.Equal(BookingError.AlreadyBooked, again.Error);
        Assert.Equal(1, roster.ConfirmedCount);
    }

    [Fact]
    public void Book_SameClientAfterCancelling_IsAllowed()
    {
        var roster = Roster();
        var client = Guid.NewGuid();
        var first = roster.Book(client, Guid.NewGuid(), Now);
        roster.Cancel(first.Booking!.Id, Now);

        var again = roster.Book(client, Guid.NewGuid(), Now.AddMinutes(5));

        Assert.True(again.Succeeded);
        Assert.Equal(BookingStatus.Confirmed, again.Booking!.Status);
    }

    [Fact]
    public void Book_AfterClassStarted_IsRejected()
    {
        var roster = Roster(startsIn: TimeSpan.FromMinutes(-1));

        var result = roster.Book(Guid.NewGuid(), Guid.NewGuid(), Now);

        Assert.Equal(BookingError.ClassAlreadyStarted, result.Error);
    }

    [Fact]
    public void Roster_IgnoresBookingsForOtherClasses()
    {
        var otherClass = new Booking(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), BookingStatus.Confirmed, Now);

        var roster = new ClassRoster(Session(capacity: 1), new[] { otherClass });

        Assert.Equal(0, roster.ConfirmedCount);
        Assert.Equal(1, roster.SpotsLeft);
    }

    [Fact]
    public void Roster_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Roster(capacity: 0));
    }

    // ---------- cancelling and the waitlist ----------

    [Fact]
    public void Cancel_ConfirmedBooking_PromotesFirstInLine()
    {
        var roster = Roster(capacity: 1);
        var holder = roster.Book(Guid.NewGuid(), Guid.NewGuid(), Now).Booking!;
        var firstInLine = roster.Book(Guid.NewGuid(), Guid.NewGuid(), Now.AddMinutes(1)).Booking!;
        var secondInLine = roster.Book(Guid.NewGuid(), Guid.NewGuid(), Now.AddMinutes(2)).Booking!;

        var result = roster.Cancel(holder.Id, Now.AddMinutes(10));

        Assert.True(result.Succeeded);
        Assert.Equal(firstInLine.Id, result.Promoted!.Id);
        Assert.Equal(BookingStatus.Confirmed, firstInLine.Status);
        Assert.Equal(BookingStatus.Waitlisted, secondInLine.Status);
        Assert.Single(roster.Waitlist);
        Assert.Equal(1, roster.ConfirmedCount);
    }

    [Fact]
    public void Cancel_WaitlistedBooking_DoesNotPromoteAnyone()
    {
        var roster = Roster(capacity: 1);
        roster.Book(Guid.NewGuid(), Guid.NewGuid(), Now);
        var waiting = roster.Book(Guid.NewGuid(), Guid.NewGuid(), Now.AddMinutes(1)).Booking!;
        var behind = roster.Book(Guid.NewGuid(), Guid.NewGuid(), Now.AddMinutes(2)).Booking!;

        var result = roster.Cancel(waiting.Id, Now.AddMinutes(10));

        Assert.Null(result.Promoted);
        Assert.Equal(BookingStatus.Waitlisted, behind.Status);
        Assert.Equal(1, roster.ConfirmedCount);
    }

    [Fact]
    public void Cancel_WithEmptyWaitlist_JustFreesTheSpot()
    {
        var roster = Roster(capacity: 2);
        var booking = roster.Book(Guid.NewGuid(), Guid.NewGuid(), Now).Booking!;

        var result = roster.Cancel(booking.Id, Now.AddMinutes(1));

        Assert.Null(result.Promoted);
        Assert.Equal(2, roster.SpotsLeft);
    }

    [Fact]
    public void Cancel_Twice_IsRejected()
    {
        var roster = Roster();
        var booking = roster.Book(Guid.NewGuid(), Guid.NewGuid(), Now).Booking!;
        roster.Cancel(booking.Id, Now);

        var again = roster.Cancel(booking.Id, Now);

        Assert.Equal(BookingError.AlreadyCancelled, again.Error);
    }

    [Fact]
    public void Cancel_UnknownBooking_IsRejected()
    {
        var result = Roster().Cancel(Guid.NewGuid(), Now);

        Assert.Equal(BookingError.BookingNotFound, result.Error);
    }

    [Fact]
    public void Cancel_InsideTwelveHours_IsFlaggedLate()
    {
        var roster = Roster(startsIn: TimeSpan.FromHours(6));
        var booking = roster.Book(Guid.NewGuid(), Guid.NewGuid(), Now).Booking!;

        var result = roster.Cancel(booking.Id, Now);

        Assert.True(result.Cancelled!.LateCancel);
    }

    [Fact]
    public void Cancel_WellBeforeClass_IsNotLate()
    {
        var roster = Roster(startsIn: TimeSpan.FromDays(2));
        var booking = roster.Book(Guid.NewGuid(), Guid.NewGuid(), Now).Booking!;

        var result = roster.Cancel(booking.Id, Now);

        Assert.False(result.Cancelled!.LateCancel);
    }

    [Fact]
    public void Cancel_AfterClassStarted_DoesNotPromote()
    {
        var roster = Roster(capacity: 1, startsIn: TimeSpan.FromHours(1));
        var holder = roster.Book(Guid.NewGuid(), Guid.NewGuid(), Now).Booking!;
        var waiting = roster.Book(Guid.NewGuid(), Guid.NewGuid(), Now).Booking!;

        var result = roster.Cancel(holder.Id, Now.AddHours(2));

        Assert.Null(result.Promoted);
        Assert.Equal(BookingStatus.Waitlisted, waiting.Status);
    }

    [Fact]
    public void Waitlist_IsFirstComeFirstServed_EvenWhenLoadedOutOfOrder()
    {
        var early = new Booking(Guid.NewGuid(), ClassId, Guid.NewGuid(), BookingStatus.Waitlisted, Now.AddMinutes(1));
        var late = new Booking(Guid.NewGuid(), ClassId, Guid.NewGuid(), BookingStatus.Waitlisted, Now.AddMinutes(5));
        var holder = new Booking(Guid.NewGuid(), ClassId, Guid.NewGuid(), BookingStatus.Confirmed, Now);

        // Rows from a database don't come back in a guaranteed order.
        var roster = new ClassRoster(Session(capacity: 1), new[] { late, holder, early });
        var result = roster.Cancel(holder.Id, Now.AddMinutes(10));

        Assert.Equal(early.Id, result.Promoted!.Id);
    }
}
