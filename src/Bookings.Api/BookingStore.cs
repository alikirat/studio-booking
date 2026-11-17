using System.Collections.Concurrent;
using StudioBooking.Domain;

namespace Bookings.Api;

/// <summary>
/// Storage for bookings. The important method is <see cref="WithClassLockAsync{T}"/>:
/// two people booking the last spot at the same moment must not both get it, so
/// everything that reads a class roster and then writes to it runs inside one lock
/// per class (a Postgres advisory lock in production).
/// </summary>
public interface IBookingStore
{
    Task InitializeAsync(CancellationToken ct);
    Task<T> WithClassLockAsync<T>(Guid classId, Func<IRosterSession, Task<T>> work, CancellationToken ct);
    Task<Booking?> GetAsync(Guid bookingId, CancellationToken ct);
    Task<IReadOnlyList<Booking>> ListForClientAsync(Guid clientId, CancellationToken ct);
    Task<IReadOnlyList<Booking>> ListForClassAsync(Guid classId, CancellationToken ct);
}

/// <summary>Reads and writes for one class, valid only inside the class lock.</summary>
public interface IRosterSession
{
    Task<IReadOnlyList<Booking>> LoadClassAsync(Guid classId, CancellationToken ct);
    Task InsertAsync(Booking booking, CancellationToken ct);
    Task UpdateAsync(Booking booking, CancellationToken ct);
}

public sealed class InMemoryBookingStore : IBookingStore, IRosterSession
{
    private readonly ConcurrentDictionary<Guid, Booking> _bookings = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task<T> WithClassLockAsync<T>(Guid classId, Func<IRosterSession, Task<T>> work, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(classId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await work(this);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<IReadOnlyList<Booking>> LoadClassAsync(Guid classId, CancellationToken ct) => ListForClassAsync(classId, ct);

    // Bookings are stored as live objects here, so "update" is a no-op:
    // the roster already changed the instance in place.
    public Task InsertAsync(Booking booking, CancellationToken ct)
    {
        _bookings[booking.Id] = booking;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Booking booking, CancellationToken ct) => Task.CompletedTask;

    public Task<Booking?> GetAsync(Guid bookingId, CancellationToken ct) =>
        Task.FromResult(_bookings.TryGetValue(bookingId, out var b) ? b : null);

    public Task<IReadOnlyList<Booking>> ListForClientAsync(Guid clientId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Booking>>(_bookings.Values.Where(b => b.ClientId == clientId).OrderBy(b => b.CreatedAt).ToList());

    public Task<IReadOnlyList<Booking>> ListForClassAsync(Guid classId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Booking>>(_bookings.Values.Where(b => b.ClassId == classId).OrderBy(b => b.CreatedAt).ToList());
}
