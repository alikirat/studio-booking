using StudioBooking.Contracts;

namespace Classes.Api;

/// <summary>
/// A small, realistic schedule so the system is usable the moment it starts.
/// IDs are fixed so the demo script and README examples can refer to them.
/// </summary>
public static class SeedData
{
    public static readonly Guid SunriseVinyasa = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid PowerPilates = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid Spin45 = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static async Task EnsureSeededAsync(IClassStore store, TimeProvider clock, ILogger logger)
    {
        if (await store.AnyAsync(CancellationToken.None))
            return;

        var now = clock.GetUtcNow();
        var tomorrow = new DateTimeOffset(now.Date.AddDays(1), TimeSpan.Zero);

        var classes = new[]
        {
            new ClassDto(SunriseVinyasa, "Sunrise Vinyasa", "Maya R.", tomorrow.AddHours(15), 60, 3),
            new ClassDto(PowerPilates, "Power Pilates", "Jordan T.", tomorrow.AddHours(17), 50, 2),
            // Starts soon, so cancelling it shows the late-cancel flag.
            new ClassDto(Spin45, "Spin 45", "Chris L.", now.AddHours(6), 45, 1),
        };

        foreach (var cls in classes)
            await store.CreateAsync(cls, CancellationToken.None);

        logger.LogInformation("Seeded {Count} classes", classes.Length);
    }
}
