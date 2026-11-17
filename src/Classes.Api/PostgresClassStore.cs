using Npgsql;
using StudioBooking.Contracts;

namespace Classes.Api;

/// <summary>
/// Postgres storage for the class schedule. This service owns the "classes"
/// database; no other service reads it directly. Other services ask over HTTP.
/// </summary>
public sealed class PostgresClassStore(string connectionString, ILogger<PostgresClassStore> logger) : IClassStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _db = NpgsqlDataSource.Create(connectionString);

    private const string Columns = "id, name, instructor, starts_at, duration_minutes, capacity";

    public async Task InitializeAsync(CancellationToken ct)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS classes (
                id               uuid PRIMARY KEY,
                name             text        NOT NULL,
                instructor       text        NOT NULL,
                starts_at        timestamptz NOT NULL,
                duration_minutes int         NOT NULL CHECK (duration_minutes > 0),
                capacity         int         NOT NULL CHECK (capacity > 0)
            );
            CREATE INDEX IF NOT EXISTS ix_classes_starts_at ON classes (starts_at);
            """;

        // Compose waits for Postgres to report healthy, but retry a few times anyway
        // so the service also survives a database restart during startup.
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

    public async Task<IReadOnlyList<ClassDto>> ListAsync(DateTimeOffset from, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand($"SELECT {Columns} FROM classes WHERE starts_at >= $1 ORDER BY starts_at");
        cmd.Parameters.AddWithValue(from == DateTimeOffset.MinValue ? DateTimeOffset.UnixEpoch : from.ToUniversalTime());

        var results = new List<ClassDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(Read(reader));
        return results;
    }

    public async Task<ClassDto?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand($"SELECT {Columns} FROM classes WHERE id = $1");
        cmd.Parameters.AddWithValue(id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task CreateAsync(ClassDto cls, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            "INSERT INTO classes (id, name, instructor, starts_at, duration_minutes, capacity) VALUES ($1, $2, $3, $4, $5, $6)");
        cmd.Parameters.AddWithValue(cls.Id);
        cmd.Parameters.AddWithValue(cls.Name);
        cmd.Parameters.AddWithValue(cls.Instructor);
        cmd.Parameters.AddWithValue(cls.StartsAt.ToUniversalTime());
        cmd.Parameters.AddWithValue(cls.DurationMinutes);
        cmd.Parameters.AddWithValue(cls.Capacity);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> AnyAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("SELECT EXISTS (SELECT 1 FROM classes)");
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static ClassDto Read(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        r.GetString(2),
        r.GetFieldValue<DateTimeOffset>(3),
        r.GetInt32(4),
        r.GetInt32(5));

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
