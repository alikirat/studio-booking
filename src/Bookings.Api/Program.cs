using Bookings.Api;
using StudioBooking.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("bookings-api");
builder.AddBookingInfrastructure();
builder.Services.AddScoped<BookingService>();

var app = builder.Build();
app.UseServiceDefaults();

await app.Services.GetRequiredService<IBookingStore>().InitializeAsync(app.Lifetime.ApplicationStopping);

app.MapPost("/bookings", async (CreateBookingRequest request, BookingService service, HttpContext http, CancellationToken ct) =>
{
    if (request.ClassId == Guid.Empty || request.ClientId == Guid.Empty)
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["classId/clientId"] = ["Both classId and clientId are required."]
        });

    var result = await service.BookAsync(request.ClassId, request.ClientId, http.GetCorrelationId(), ct);
    return result.Outcome == Outcome.Ok
        ? Results.Created($"/bookings/{result.Value!.Id}", result.Value)
        : ToProblem(result.Outcome, result.Message);
});

app.MapDelete("/bookings/{id:guid}", async (Guid id, BookingService service, HttpContext http, CancellationToken ct) =>
{
    var result = await service.CancelAsync(id, http.GetCorrelationId(), ct);
    return result.Outcome == Outcome.Ok ? Results.Ok(result.Value) : ToProblem(result.Outcome, result.Message);
});

app.MapGet("/bookings/{id:guid}", async (Guid id, IBookingStore store, CancellationToken ct) =>
    await store.GetAsync(id, ct) is { } b ? Results.Ok(BookingResponse.From(b)) : Results.NotFound());

app.MapGet("/bookings", async (Guid clientId, IBookingStore store, CancellationToken ct) =>
    Results.Ok((await store.ListForClientAsync(clientId, ct)).Select(b => BookingResponse.From(b))));

app.MapGet("/classes/{classId:guid}/roster", async (Guid classId, BookingService service, CancellationToken ct) =>
{
    var result = await service.GetRosterAsync(classId, ct);
    return result.Outcome == Outcome.Ok ? Results.Ok(result.Value) : ToProblem(result.Outcome, result.Message);
});

app.Run();

static IResult ToProblem(Outcome outcome, string? message) => outcome switch
{
    Outcome.NotFound => Results.Problem(message, statusCode: StatusCodes.Status404NotFound),
    Outcome.Conflict => Results.Problem(message, statusCode: StatusCodes.Status409Conflict),
    Outcome.Unavailable => Results.Problem(message, statusCode: StatusCodes.Status503ServiceUnavailable),
    _ => Results.Problem(message)
};

public sealed record CreateBookingRequest(Guid ClassId, Guid ClientId);
