using StudioBooking.ServiceDefaults;

namespace Bookings.Api;

public static class InfrastructureRegistration
{
    /// <summary>
    /// Wires storage, the Classes client, and messaging from configuration:
    ///   Storage:Provider   = Postgres | InMemory
    ///   Messaging:Provider = RabbitMq | Http | None
    /// Docker Compose sets Postgres + RabbitMq; a bare "dotnet run" uses InMemory + Http.
    /// </summary>
    public static void AddBookingInfrastructure(this WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        builder.Services.AddSingleton(TimeProvider.System);

        // Storage
        if (string.Equals(config["Storage:Provider"], "Postgres", StringComparison.OrdinalIgnoreCase))
        {
            var cs = config.GetConnectionString("BookingsDb")
                ?? throw new InvalidOperationException("ConnectionStrings:BookingsDb is required when Storage:Provider is Postgres.");
            builder.Services.AddSingleton<IBookingStore>(sp =>
                new PostgresBookingStore(cs, sp.GetRequiredService<ILogger<PostgresBookingStore>>()));
        }
        else
        {
            builder.Services.AddSingleton<IBookingStore, InMemoryBookingStore>();
        }

        // Classes service client
        builder.Services
            .AddHttpClient<IClassCatalog, HttpClassCatalog>(c =>
            {
                c.BaseAddress = new Uri(config["Services:ClassesApi"] ?? "http://localhost:5101");
                c.Timeout = TimeSpan.FromSeconds(3);
            })
            .AddHttpMessageHandler<CorrelationIdHandler>();

        // Messaging
        switch (config["Messaging:Provider"]?.ToLowerInvariant() ?? "http")
        {
            case "rabbitmq":
                builder.Services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
                break;
            case "http":
                builder.Services.AddHttpClient<IEventPublisher, HttpEventPublisher>(c =>
                {
                    c.BaseAddress = new Uri(config["Services:NotificationsApi"] ?? "http://localhost:5103");
                    c.Timeout = TimeSpan.FromSeconds(3);
                });
                break;
            default:
                builder.Services.AddSingleton<IEventPublisher, NoOpEventPublisher>();
                break;
        }
    }
}
