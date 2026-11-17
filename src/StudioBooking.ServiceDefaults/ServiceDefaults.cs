using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace StudioBooking.ServiceDefaults;

/// <summary>
/// Setup every service shares: structured logs, correlation IDs, request timing,
/// and a health endpoint. Keeping it in one place means all three services
/// produce logs with the same field names, which is what makes them searchable
/// together in Kibana.
/// </summary>
public static class ServiceDefaultsExtensions
{
    public const string CorrelationHeader = "X-Correlation-Id";

    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder, string serviceName)
    {
        builder.Logging.ClearProviders();

        // "json" in containers so Filebeat can ship fields straight to Elasticsearch;
        // plain text locally because it is easier to read in a terminal.
        if (string.Equals(builder.Configuration["Logging:Format"], "json", StringComparison.OrdinalIgnoreCase))
        {
            builder.Logging.AddJsonConsole(o =>
            {
                o.IncludeScopes = true;
                o.UseUtcTimestamp = true;
                o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            });
        }
        else
        {
            builder.Logging.AddSimpleConsole(o =>
            {
                o.IncludeScopes = true;
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
        }

        builder.Services.AddSingleton(new ServiceInfo(serviceName));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddTransient<CorrelationIdHandler>();
        builder.Services.AddHealthChecks();
        builder.Services.AddProblemDetails();
        return builder;
    }

    public static WebApplication UseServiceDefaults(this WebApplication app)
    {
        app.UseMiddleware<CorrelationAndTimingMiddleware>();
        app.UseExceptionHandler();
        app.MapHealthChecks("/health");
        return app;
    }

    public static string? GetCorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(CorrelationHeader, out var value) ? value as string : null;
}

public sealed record ServiceInfo(string Name);

/// <summary>
/// Log scope that shows up as separate fields in JSON logs (so Kibana can filter on
/// CorrelationId) and as "CorrelationId:abc Service:x" in plain-text logs.
/// A bare Dictionary works for JSON but prints its type name in text logs.
/// </summary>
public sealed class LogScope(params (string Key, object? Value)[] fields) : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly KeyValuePair<string, object?>[] _fields =
        fields.Select(f => new KeyValuePair<string, object?>(f.Key, f.Value)).ToArray();

    public KeyValuePair<string, object?> this[int index] => _fields[index];
    public int Count => _fields.Length;
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)_fields).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() => string.Join(" ", _fields.Select(f => $"{f.Key}:{f.Value}"));
}

/// <summary>
/// Reuses the caller's correlation ID (or starts a new one), puts it on every log
/// line written during the request, and logs how long the request took.
/// One ID per user action is what lets you follow a booking across all three services.
/// </summary>
public sealed class CorrelationAndTimingMiddleware(
    RequestDelegate next,
    ILogger<CorrelationAndTimingMiddleware> logger,
    ServiceInfo service)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(ServiceDefaultsExtensions.CorrelationHeader, out var header)
                            && !string.IsNullOrWhiteSpace(header)
            ? header.ToString()
            : Guid.NewGuid().ToString("N");

        context.Items[ServiceDefaultsExtensions.CorrelationHeader] = correlationId;
        context.Response.Headers[ServiceDefaultsExtensions.CorrelationHeader] = correlationId;

        using var scope = logger.BeginScope(new LogScope(("CorrelationId", correlationId), ("Service", service.Name)));

        var timer = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            timer.Stop();
            // Log the route template ("/bookings/{id}"), not the raw path, so
            // dashboards group requests by endpoint instead of by individual ID.
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? context.Request.Path.Value;
            if (route != "/health")
            {
                logger.LogInformation(
                    "HTTP {Method} {Route} responded {StatusCode} in {ElapsedMs} ms",
                    context.Request.Method, route, context.Response.StatusCode, Math.Round(timer.Elapsed.TotalMilliseconds, 1));
            }
        }
    }
}

/// <summary>Copies the current request's correlation ID onto outgoing HTTP calls to other services.</summary>
public sealed class CorrelationIdHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var correlationId = accessor.HttpContext?.GetCorrelationId();
        if (correlationId is not null && !request.Headers.Contains(ServiceDefaultsExtensions.CorrelationHeader))
            request.Headers.Add(ServiceDefaultsExtensions.CorrelationHeader, correlationId);

        return base.SendAsync(request, cancellationToken);
    }
}
