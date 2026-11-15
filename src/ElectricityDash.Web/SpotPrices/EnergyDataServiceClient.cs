using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ElectricityDash.Web.SpotPrices;

public sealed record EnergyDataServiceOptions
{
    public Uri Endpoint { get; init; } = new("https://api.energidataservice.dk/");
    public string PriceArea { get; init; } = "DK1";

    /// <summary>Base delay between retries. Tests lower this to keep failure scenarios fast.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Retries after a 429 / 5xx. When the server sends Retry-After it is honoured instead of <see cref="RetryDelay"/>.
    /// </summary>
    public int MaxRetries { get; init; } = 4;

    /// <summary>
    /// Upper bound for one call including all retries and waits. Must exceed the server's Retry-After
    /// (seen up to 60s when rate limited), otherwise the wait is cut short and the call fails.
    /// </summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromMinutes(3);
}

/// <summary>A day-ahead price for one delivery period (15 minutes from 2025-10-01, hourly before).</summary>
public sealed record DayAheadPrice(DateTimeOffset PeriodStartUtc, decimal DkkPerMwh);

public sealed class EnergyDataServiceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Typed client for the Energi Data Service <c>DayAheadPrices</c> dataset (15-minute resolution, data from 2025-10-01).
/// Retries and timeouts come from the resilience handler registered in <c>Program</c>.
/// </summary>
public sealed class EnergyDataServiceClient(HttpClient http)
{
    public async Task<IReadOnlyList<DayAheadPrice>> GetDayAheadPricesAsync(
        string priceArea, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        // start is inclusive, end is exclusive. timezone=utc makes start/end and the response UTC.
        var filter = Uri.EscapeDataString($$"""{"PriceArea":["{{priceArea}}"]}""");
        var url = $"dataset/DayAheadPrices?start={Format(fromUtc)}&end={Format(toUtc)}"
                  + $"&filter={filter}&sort=TimeUTC&timezone=utc&limit=0";

        Response? body;
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new EnergyDataServiceException(
                    $"Energi Data Service returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            body = await response.Content.ReadFromJsonAsync<Response>(ct);
        }
        // Network errors, bad JSON, and the resilience handler's timeout / open-circuit exceptions all become one error type.
        catch (Exception e) when (e is not EnergyDataServiceException && !ct.IsCancellationRequested)
        {
            throw new EnergyDataServiceException("Calling Energi Data Service failed.", e);
        }

        if (body?.Records is null)
        {
            throw new EnergyDataServiceException("Energi Data Service returned an unexpected response body.");
        }

        return body.Records
            .Select(r => new DayAheadPrice(
                // The API returns UTC timestamps without an offset, so mark them as UTC explicitly.
                new DateTimeOffset(DateTime.SpecifyKind(r.TimeUtc, DateTimeKind.Utc)),
                r.DayAheadPriceDkk ?? throw new EnergyDataServiceException(
                    $"Missing DKK price for {r.TimeUtc:O} in {priceArea}.")))
            .ToList();
    }

    private static string Format(DateTimeOffset utc) =>
        utc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    private sealed record Response(IReadOnlyList<Record>? Records);

    private sealed record Record(
        [property: JsonPropertyName("TimeUTC")] DateTime TimeUtc,
        [property: JsonPropertyName("DayAheadPriceDKK")] decimal? DayAheadPriceDkk);
}
