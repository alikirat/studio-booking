using Npgsql;
using NpgsqlTypes;
using StudioBooking.Domain;

namespace Bookings.Api;

/// <summary>
/// Postgres storage for bookings, in the Bookings service's own database.
///
/// Concurrency: <see cref="WithClassLockAsync{T}"/> opens a transaction and takes
/// pg_advisory_xact_lock keyed on the class ID. Every booking or cancellation for
/// that class waits its turn, while different classes proceed in parallel. The lock
/// is released automatically on commit or rollback. The partial unique index is a
/// second line of defense against double-booking.
/// </summary>
public sealed class PostgresBookingStore(string connectionString, ILogger<PostgresBookingStore> logger) : IBookingStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _db = NpgsqlDataSource.Create(connectionString);

    private const string Columns = "id, class_id, client_id, status, created_at, cancelled_at, promoted_at, late_cancel";

    public async Task InitializeAsync(CancellationToken ct)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS bookings (
                id           uuid PRIMARY KEY,
                class_id     uuid        NOT NULL,
                client_id    uuid        NOT NULL,
                status       text        NOT NULL,
                created_at   timestamptz NOT NULL,
                cancelled_at timestamptz NULL,
                promoted_at  timestamptz NULL,
                late_cancel  boolean     NOT NULL DEFAULT false
            );
            CREATE INDEX IF NOT EXISTS ix_bookings_class ON bookings (class_id);
            CREATE INDEX IF NOT EXISTS ix_bookings_client ON bookings (client_id);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_bookings_active_client_class
                ON bookings (class_id, client_id) WHERE status <> 'Cancelled';
            """;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var cmd = _db.CreateCommand(schema);
                await cmd.ExecuteNonQueryAsync(ct);
                return;
            }
            catch (NpgsqlException ex) when (attempt < 10)
            {
                logger.LogWarning(ex, "Database not ready (attempt {Attempt}), retrying", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    public async Task<T> WithClassLockAsync<T>(Guid classId, Func<IRosterSession, Task<T>> work, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", conn, tx))
        {
            lockCmd.Parameters.AddWithValue(LockKey(classId));
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        var result = await work(new Session(conn, tx));
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<Booking?> GetAsync(Guid bookingId, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand($"SELECT {Columns} FROM bookings WHERE id = $1");
        cmd.Parameters.AddWithValue(bookingId);
        var rows = await ReadAllAsync(cmd, ct);
        return rows.FirstOrDefault();
    }

    public async Task<IReadOnlyList<Booking>> ListForClientAsync(Guid clientId, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand($"SELECT {Columns} FROM bookings WHERE client_id = $1 ORDER BY created_at");
        cmd.Parameters.AddWithValue(clientId);
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<Booking>> ListForClassAsync(Guid classId, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand($"SELECT {Columns} FROM bookings WHERE class_id = $1 ORDER BY created_at");
        cmd.Parameters.AddWithValue(classId);
        return await ReadAllAsync(cmd, ct);
    }

    /// <summary>Advisory locks take a 64-bit key; the first 8 bytes of the GUID are plenty unique for this.</summary>
    private static long LockKey(Guid classId) => BitConverter.ToInt64(classId.ToByteArray(), 0);

    private static async Task<IReadOnlyList<Booking>> ReadAllAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var results = new List<Booking>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            results.Add(new Booking(
                id: r.GetGuid(0),
                classId: r.GetGuid(1),
                clientId: r.GetGuid(2),
                status: Enum.Parse<BookingStatus>(r.GetString(3)),
                createdAt: r.GetFieldValue<DateTimeOffset>(4),
                cancelledAt: r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
                promotedAt: r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
                lateCancel: r.GetBoolean(7)));
        }
        return results;
    }

    private static NpgsqlParameter Timestamp(DateTimeOffset? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.TimestampTz,
        Value = value.HasValue ? value.Value.ToUniversalTime() : DBNull.Value
    };

    private sealed class Session(NpgsqlConnection conn, NpgsqlTransaction tx) : IRosterSession
    {
        public async Task<IReadOnlyList<Booking>> LoadClassAsync(Guid classId, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM bookings WHERE class_id = $1", conn, tx);
            cmd.Parameters.AddWithValue(classId);
            return await ReadAllAsync(cmd, ct);
        }

        public async Task InsertAsync(Booking b, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(
                $"INSERT INTO bookings ({Columns}) VALUES ($1, $2, $3, $4, $5, $6, $7, $8)", conn, tx);
            cmd.Parameters.AddWithValue(b.Id);
            cmd.Parameters.AddWithValue(b.ClassId);
            cmd.Parameters.AddWithValue(b.ClientId);
            cmd.Parameters.AddWithValue(b.Status.ToString());
            cmd.Parameters.Add(Timestamp(b.CreatedAt));
            cmd.Parameters.Add(Timestamp(b.CancelledAt));
            cmd.Parameters.Add(Timestamp(b.PromotedAt));
            cmd.Parameters.AddWithValue(b.LateCancel);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task UpdateAsync(Booking b, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE bookings SET status = $2, cancelled_at = $3, promoted_at = $4, late_cancel = $5 WHERE id = $1", conn, tx);
            cmd.Parameters.AddWithValue(b.Id);
            cmd.Parameters.AddWithValue(b.Status.ToString());
            cmd.Parameters.Add(Timestamp(b.CancelledAt));
            cmd.Parameters.Add(Timestamp(b.PromotedAt));
            cmd.Parameters.AddWithValue(b.LateCancel);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
