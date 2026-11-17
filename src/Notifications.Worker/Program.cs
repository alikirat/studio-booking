using Notifications.Worker;
using StudioBooking.Contracts;
using StudioBooking.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("notifications-worker");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<NotificationHandler>();

// Messaging:Provider = RabbitMq (Docker) consumes from the queue.
// Messaging:Provider = Http (bare dotnet run) accepts events on POST /dev/events instead.
var messaging = builder.Configuration["Messaging:Provider"]?.ToLowerInvariant() ?? "http";
if (messaging == "rabbitmq")
{
    builder.Services.AddSingleton<RabbitMqConsumer>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RabbitMqConsumer>());
    // /health stays unhealthy until the queue exists, so nothing starts publishing too early.
    builder.Services.AddHealthChecks().AddCheck<RabbitMqConsumer.HealthCheck>("rabbitmq-consumer");
}

var app = builder.Build();
app.UseServiceDefaults();

// What clients would have received. Useful for the demo and for checking the flow end to end.
app.MapGet("/notifications", (NotificationHandler handler, Guid? clientId) =>
    Results.Ok(handler.Recent.Where(n => clientId is null || n.ClientId == clientId)));

if (messaging == "http")
{
    app.MapPost("/dev/events", (BookingEvent evt, NotificationHandler handler) =>
    {
        handler.Handle(evt);
        return Results.Accepted();
    });
}

app.Run();
