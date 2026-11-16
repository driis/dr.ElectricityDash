using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace ElectricityDash.Web.Eloverblik;

public sealed record EloverblikOptions
{
    public Uri Endpoint { get; init; } = new("https://api.eloverblik.dk/customerapi/api/");

    /// <summary>Long-lived refresh token from eloverblik.dk. A secret: user-secrets locally, environment variable when deployed.</summary>
    public string? RefreshToken { get; init; }

    /// <summary>Optional. When unset the (single) consumption metering point is discovered from Eloverblik.</summary>
    public string? MeteringPointId { get; init; }

    /// <summary>Base delay between retries. Tests lower this to keep failure scenarios fast.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Retries after a 429 / 5xx. A Retry-After header is honoured instead of <see cref="RetryDelay"/>.</summary>
    public int MaxRetries { get; init; } = 4;

    /// <summary>Upper bound for one HTTP attempt. Two years of hourly data is about 1.5 MB, so the default is generous.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Upper bound for one call including all retries and waits.</summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>One metered hour. <paramref name="Quality"/> is Eloverblik's code (A04 = read from the meter, A03 = estimated).</summary>
public sealed record HourlyConsumption(DateTimeOffset HourStartUtc, decimal Kwh, string Quality);

public sealed record ChargePrice(int Position, decimal Dkk);

/// <summary>One version of a tariff, subscription or fee as returned by <c>getcharges</c>.</summary>
public sealed record Charge(
    string PriceId,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    string Kind,
    string Name,
    string? Description,
    string? Owner,
    string PeriodType,
    decimal? Quantity,
    IReadOnlyList<ChargePrice> Prices);

public static class ChargeKind
{
    public const string Tariff = "tariff";
    public const string Subscription = "subscription";
    public const string Fee = "fee";
}

public sealed class EloverblikException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Typed client for the Eloverblik Customer API. Exchanges the refresh token for a short-lived data access token
/// once per client instance (one per ingest request). Retries and timeouts come from the resilience handler in <c>Program</c>.
/// </summary>
public sealed class EloverblikClient(HttpClient http, Microsoft.Extensions.Options.IOptions<EloverblikOptions> options)
{
    private const string ConsumptionMeteringPointType = "E17";
    private string? _accessToken;

    /// <summary>The configured metering point, or the single consumption metering point on the account.</summary>
    public async Task<string> GetMeteringPointIdAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.Value.MeteringPointId)) return options.Value.MeteringPointId;

        var response = await SendAsync<ListResponse<MeteringPoint>>(HttpMethod.Get, "meteringpoints/meteringpoints?includeAll=false", null, ct);
        var ids = (response.Result ?? [])
            .Where(m => m.TypeOfMp == ConsumptionMeteringPointType && !string.IsNullOrEmpty(m.MeteringPointId))
            .Select(m => m.MeteringPointId!)
            .ToList();

        return ids.Count switch
        {
            1 => ids[0],
            0 => throw new EloverblikException("Eloverblik has no consumption metering point for this token."),
            _ => throw new EloverblikException(
                "Eloverblik has several consumption metering points, set Eloverblik:MeteringPointId to choose one.")
        };
    }

    /// <summary>
    /// Hourly consumption for the Danish calendar days from <paramref name="from"/> up to but excluding <paramref name="toExclusive"/>
    /// (Eloverblik's <c>dateTo</c> is exclusive). Hours Eloverblik has not delivered yet are simply absent.
    /// </summary>
    public async Task<IReadOnlyList<HourlyConsumption>> GetHourlyConsumptionAsync(
        string meteringPointId, DateOnly from, DateOnly toExclusive, CancellationToken ct)
    {
        var path = $"meterdata/gettimeseries/{from:yyyy-MM-dd}/{toExclusive:yyyy-MM-dd}/Hour";
        var response = await SendAsync<ListResponse<TimeSeriesResult>>(HttpMethod.Post, path, MeteringPointBody(meteringPointId), ct);

        var hours = new List<HourlyConsumption>();
        foreach (var result in response.Result ?? [])
        {
            EnsureSuccess(result.Success, result.ErrorCode, result.ErrorText);
            foreach (var series in result.MarketDocument?.TimeSeries ?? [])
            {
                if (!string.Equals(series.Unit, "KWH", StringComparison.OrdinalIgnoreCase))
                {
                    throw new EloverblikException($"Unexpected consumption unit '{series.Unit}', expected KWH.");
                }

                foreach (var period in series.Period ?? [])
                {
                    hours.AddRange(ToHours(period));
                }
            }
        }

        return hours;
    }

    public async Task<IReadOnlyList<Charge>> GetChargesAsync(string meteringPointId, CancellationToken ct)
    {
        var response = await SendAsync<ListResponse<ChargesResult>>(
            HttpMethod.Post, "meteringpoints/meteringpoint/getcharges", MeteringPointBody(meteringPointId), ct);

        var charges = new List<Charge>();
        foreach (var entry in response.Result ?? [])
        {
            EnsureSuccess(entry.Success, entry.ErrorCode, entry.ErrorText);
            if (entry.Result is not { } set) continue;

            charges.AddRange((set.Tariffs ?? []).Select(t => ToCharge(t, ChargeKind.Tariff, quantity: null,
                (t.Prices ?? []).Select(ToPrice).ToList())));
            charges.AddRange((set.Subscriptions ?? []).Select(s => ToCharge(s, ChargeKind.Subscription, s.Quantity, [new ChargePrice(1, s.Price)])));
            charges.AddRange((set.Fees ?? []).Select(f => ToCharge(f, ChargeKind.Fee, f.Quantity, [new ChargePrice(1, f.Price)])));
        }

        return charges;
    }

    private static IEnumerable<HourlyConsumption> ToHours(Period period)
    {
        if (period.Resolution != "PT1H")
        {
            throw new EloverblikException($"Unexpected resolution '{period.Resolution}', expected PT1H for the Hour aggregation.");
        }

        foreach (var point in period.Point ?? [])
        {
            // A missing quantity means no reading for that position. Skip it so the hour stays absent rather than zero.
            if (string.IsNullOrWhiteSpace(point.Quantity)) continue;

            if (!int.TryParse(point.Position, NumberStyles.None, CultureInfo.InvariantCulture, out var position) || position < 1
                || !decimal.TryParse(point.Quantity, NumberStyles.Number, CultureInfo.InvariantCulture, out var kwh))
            {
                throw new EloverblikException($"Unparseable consumption point (position '{point.Position}', quantity '{point.Quantity}').");
            }

            // Positions are 1-based hours from the period start, which is UTC. DST days simply have 23 or 25 positions.
            var hourStart = period.TimeInterval.Start.ToUniversalTime().AddHours(position - 1);
            if (hourStart >= period.TimeInterval.End)
            {
                throw new EloverblikException($"Consumption position {position} lies outside its period.");
            }

            yield return new HourlyConsumption(hourStart, kwh, point.Quality ?? "");
        }
    }

    private static Charge ToCharge(ChargeDto dto, string kind, decimal? quantity, IReadOnlyList<ChargePrice> prices) => new(
        dto.PriceId ?? throw new EloverblikException($"Charge '{dto.Name}' has no priceId."),
        dto.ValidFromDate ?? throw new EloverblikException($"Charge '{dto.Name}' has no validFromDate."),
        dto.ValidToDate,
        kind,
        dto.Name ?? dto.PriceId,
        dto.Description,
        dto.Owner,
        dto.PeriodType ?? throw new EloverblikException($"Charge '{dto.Name}' has no periodType."),
        quantity,
        prices);

    private static ChargePrice ToPrice(TariffPrice p) =>
        int.TryParse(p.Position, NumberStyles.None, CultureInfo.InvariantCulture, out var position) && position >= 1
            ? new ChargePrice(position, p.Price)
            : throw new EloverblikException($"Unparseable tariff price position '{p.Position}'.");

    private static void EnsureSuccess(bool success, int errorCode, string? errorText)
    {
        if (!success) throw new EloverblikException($"Eloverblik reported error {errorCode}: {errorText}");
    }

    private static object MeteringPointBody(string meteringPointId) =>
        new { meteringPoints = new { meteringPoint = new[] { meteringPointId } } };

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null) return _accessToken;

        var refreshToken = options.Value.RefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("Eloverblik:RefreshToken is not configured.");
        }

        var response = await SendCoreAsync<TokenResponse>(HttpMethod.Get, "token", null, refreshToken, ct);
        return _accessToken = response.Result
            ?? throw new EloverblikException("Eloverblik returned no data access token.");
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct) =>
        await SendCoreAsync<T>(method, path, body, await GetAccessTokenAsync(ct), ct);

    private async Task<T> SendCoreAsync<T>(HttpMethod method, string path, object? body, string bearer, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (body is not null) request.Content = JsonContent.Create(body);

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new EloverblikException(
                    $"Eloverblik returned HTTP {(int)response.StatusCode} {response.ReasonPhrase} for {path.Split('?')[0]}.");
            }

            return await response.Content.ReadFromJsonAsync<T>(ct)
                   ?? throw new EloverblikException("Eloverblik returned an empty response body.");
        }
        // Network errors, bad JSON, and the resilience handler's timeout / open-circuit exceptions all become one error type.
        catch (Exception e) when (e is not (EloverblikException or InvalidOperationException) && !ct.IsCancellationRequested)
        {
            throw new EloverblikException("Calling Eloverblik failed.", e);
        }
    }

    private sealed record TokenResponse(string? Result);

    private sealed record ListResponse<T>(IReadOnlyList<T>? Result);

    private sealed record MeteringPoint(string? MeteringPointId, string? TypeOfMp);

    private sealed record TimeSeriesResult(
        [property: JsonPropertyName("MyEnergyData_MarketDocument")] MarketDocument? MarketDocument,
        bool Success, int ErrorCode, string? ErrorText);

    private sealed record MarketDocument(IReadOnlyList<TimeSeries>? TimeSeries);

    private sealed record TimeSeries(
        [property: JsonPropertyName("measurement_Unit.name")] string? Unit,
        IReadOnlyList<Period>? Period);

    private sealed record Period(string Resolution, TimeInterval TimeInterval, IReadOnlyList<Point>? Point);

    private sealed record TimeInterval(DateTimeOffset Start, DateTimeOffset End);

    private sealed record Point(
        string? Position,
        [property: JsonPropertyName("out_Quantity.quantity")] string? Quantity,
        [property: JsonPropertyName("out_Quantity.quality")] string? Quality);

    private sealed record ChargesResult(ChargeSet? Result, bool Success, int ErrorCode, string? ErrorText);

    private sealed record ChargeSet(
        IReadOnlyList<TariffDto>? Tariffs, IReadOnlyList<SubscriptionDto>? Subscriptions, IReadOnlyList<SubscriptionDto>? Fees);

    private record ChargeDto(
        string? PriceId, string? Name, string? Description, string? Owner,
        DateTimeOffset? ValidFromDate, DateTimeOffset? ValidToDate, string? PeriodType);

    private sealed record TariffDto(
        string? PriceId, string? Name, string? Description, string? Owner,
        DateTimeOffset? ValidFromDate, DateTimeOffset? ValidToDate, string? PeriodType,
        IReadOnlyList<TariffPrice>? Prices)
        : ChargeDto(PriceId, Name, Description, Owner, ValidFromDate, ValidToDate, PeriodType);

    private sealed record SubscriptionDto(
        string? PriceId, string? Name, string? Description, string? Owner,
        DateTimeOffset? ValidFromDate, DateTimeOffset? ValidToDate, string? PeriodType,
        decimal Price, decimal? Quantity)
        : ChargeDto(PriceId, Name, Description, Owner, ValidFromDate, ValidToDate, PeriodType);

    private sealed record TariffPrice(string? Position, decimal Price);
}
