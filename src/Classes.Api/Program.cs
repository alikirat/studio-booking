using Classes.Api;
using StudioBooking.Contracts;
using StudioBooking.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("classes-api");
builder.AddClassStore();

var app = builder.Build();
app.UseServiceDefaults();

await app.Services.GetRequiredService<IClassStore>().InitializeAsync(app.Lifetime.ApplicationStopping);
await SeedData.EnsureSeededAsync(app.Services.GetRequiredService<IClassStore>(), TimeProvider.System, app.Logger);

var classes = app.MapGroup("/classes");

classes.MapGet("/", async (IClassStore store, TimeProvider clock, bool? includePast, CancellationToken ct) =>
{
    var from = includePast == true ? DateTimeOffset.MinValue : clock.GetUtcNow().AddHours(-1);
    return Results.Ok(await store.ListAsync(from, ct));
});

classes.MapGet("/{id:guid}", async (Guid id, IClassStore store, CancellationToken ct) =>
    await store.GetAsync(id, ct) is { } cls ? Results.Ok(cls) : Results.NotFound());

classes.MapPost("/", async (CreateClassRequest request, IClassStore store, TimeProvider clock, ILogger<Program> logger, CancellationToken ct) =>
{
    var errors = request.Validate(clock.GetUtcNow());
    if (errors.Count > 0)
        return Results.ValidationProblem(errors);

    var cls = new ClassDto(
        Guid.NewGuid(),
        request.Name.Trim(),
        request.Instructor.Trim(),
        request.StartsAt.ToUniversalTime(),
        request.DurationMinutes,
        request.Capacity);

    await store.CreateAsync(cls, ct);
    logger.LogInformation("Class {ClassId} '{ClassName}' scheduled for {StartsAt} with capacity {Capacity}",
        cls.Id, cls.Name, cls.StartsAt, cls.Capacity);

    return Results.Created($"/classes/{cls.Id}", cls);
});

app.Run();

public sealed record CreateClassRequest(string Name, string Instructor, DateTimeOffset StartsAt, int DurationMinutes, int Capacity)
{
    public Dictionary<string, string[]> Validate(DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(Name)) errors["name"] = ["Name is required."];
        if (string.IsNullOrWhiteSpace(Instructor)) errors["instructor"] = ["Instructor is required."];
        if (StartsAt <= now) errors["startsAt"] = ["Classes must be scheduled in the future."];
        if (DurationMinutes is < 15 or > 240) errors["durationMinutes"] = ["Duration must be between 15 and 240 minutes."];
        if (Capacity is < 1 or > 200) errors["capacity"] = ["Capacity must be between 1 and 200."];
        return errors;
    }
}
