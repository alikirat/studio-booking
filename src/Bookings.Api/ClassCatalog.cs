using System.Net;
using StudioBooking.Contracts;

namespace Bookings.Api;

/// <summary>How the Bookings service learns about a class. It never touches the Classes database.</summary>
public interface IClassCatalog
{
    /// <returns>The class, or null if it does not exist.</returns>
    /// <exception cref="ClassCatalogUnavailableException">The Classes service could not be reached.</exception>
    Task<ClassDto?> GetClassAsync(Guid classId, CancellationToken ct);
}

public sealed class ClassCatalogUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Synchronous call to the Classes service. A booking decision needs the
/// current capacity and start time, so this has to be a request/response call
/// rather than an event. Short timeout plus one retry, because a GET is safe to repeat.
/// </summary>
public sealed class HttpClassCatalog(HttpClient http, ILogger<HttpClassCatalog> logger) : IClassCatalog
{
    private const int MaxAttempts = 2;

    public async Task<ClassDto?> GetClassAsync(Guid classId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync($"/classes/{classId}", ct);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<ClassDto>(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt >= MaxAttempts)
                {
                    logger.LogError(ex, "Classes service unavailable after {Attempts} attempts for class {ClassId}", attempt, classId);
                    throw new ClassCatalogUnavailableException("The class schedule is temporarily unavailable.", ex);
                }

                logger.LogWarning("Classes service call failed (attempt {Attempt}), retrying: {Error}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
            }
        }
    }
}
