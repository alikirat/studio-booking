using System.Collections.Concurrent;
using StudioBooking.Contracts;

namespace Classes.Api;

public interface IClassStore
{
    Task InitializeAsync(CancellationToken ct);
    Task<IReadOnlyList<ClassDto>> ListAsync(DateTimeOffset from, CancellationToken ct);
    Task<ClassDto?> GetAsync(Guid id, CancellationToken ct);
    Task CreateAsync(ClassDto cls, CancellationToken ct);
    Task<bool> AnyAsync(CancellationToken ct);
}

/// <summary>Used for quick local runs and tests. Data disappears when the process stops.</summary>
public sealed class InMemoryClassStore : IClassStore
{
    private readonly ConcurrentDictionary<Guid, ClassDto> _classes = new();

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<ClassDto>> ListAsync(DateTimeOffset from, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ClassDto>>(_classes.Values
            .Where(c => c.StartsAt >= from)
            .OrderBy(c => c.StartsAt)
            .ToList());

    public Task<ClassDto?> GetAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(_classes.TryGetValue(id, out var cls) ? cls : null);

    public Task CreateAsync(ClassDto cls, CancellationToken ct)
    {
        _classes[cls.Id] = cls;
        return Task.CompletedTask;
    }

    public Task<bool> AnyAsync(CancellationToken ct) => Task.FromResult(!_classes.IsEmpty);
}

public static class ClassStoreRegistration
{
    /// <summary>Picks the store from config: Storage:Provider = Postgres (default in Docker) or InMemory.</summary>
    public static void AddClassStore(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(TimeProvider.System);

        var provider = builder.Configuration["Storage:Provider"] ?? "InMemory";
        if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = builder.Configuration.GetConnectionString("ClassesDb")
                ?? throw new InvalidOperationException("ConnectionStrings:ClassesDb is required when Storage:Provider is Postgres.");
            builder.Services.AddSingleton<IClassStore>(sp =>
                new PostgresClassStore(connectionString, sp.GetRequiredService<ILogger<PostgresClassStore>>()));
        }
        else
        {
            builder.Services.AddSingleton<IClassStore, InMemoryClassStore>();
        }
    }
}
