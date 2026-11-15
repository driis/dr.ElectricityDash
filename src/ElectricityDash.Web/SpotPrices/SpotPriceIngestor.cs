using Microsoft.Extensions.Options;

namespace ElectricityDash.Web.SpotPrices;

public sealed record IngestResult(int HoursStored);

public sealed class SpotPriceIngestor(
    EnergyDataServiceClient client,
    SpotPriceRepository repository,
    IOptions<EnergyDataServiceOptions> options)
{
    private static readonly TimeZoneInfo Copenhagen = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");

    /// <summary>Fetches prices for the Danish calendar days <paramref name="from"/> to <paramref name="to"/> (inclusive) and upserts them as hourly prices.</summary>
    public async Task<IngestResult> IngestAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var priceArea = options.Value.PriceArea;
        var (startUtc, endUtc) = DanishDaysToUtc(from, to);

        var prices = await client.GetDayAheadPricesAsync(priceArea, startUtc, endUtc, ct);
        var hourly = HourlyAggregation.ToHourly(prices);
        await repository.UpsertAsync(priceArea, hourly, ct);

        return new IngestResult(hourly.Count);
    }

    /// <summary>
    /// Start of <paramref name="from"/> to the start of the day after <paramref name="to"/> in Danish time, as UTC.
    /// Aligned to whole days (and therefore whole hours), also across DST changes where a day is 23 or 25 hours.
    /// </summary>
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) DanishDaysToUtc(DateOnly from, DateOnly to) =>
        (StartOfDayUtc(from), StartOfDayUtc(to.AddDays(1)));

    private static DateTimeOffset StartOfDayUtc(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Copenhagen), TimeSpan.Zero);
    }

    /// <summary>The Danish calendar date for an instant.</summary>
    public static DateOnly DanishDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Copenhagen).DateTime);
}
