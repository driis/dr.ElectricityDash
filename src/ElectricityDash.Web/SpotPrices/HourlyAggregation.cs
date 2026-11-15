namespace ElectricityDash.Web.SpotPrices;

public sealed record HourlySpotPrice(DateTimeOffset HourStartUtc, decimal DkkPerMwh);

public static class HourlyAggregation
{
    /// <summary>
    /// Billing is hourly (see CLAUDE.md), so periods shorter than an hour are averaged into the hour they start in.
    /// Hourly input passes through unchanged. Output is ordered by hour.
    /// </summary>
    public static IReadOnlyList<HourlySpotPrice> ToHourly(IEnumerable<DayAheadPrice> prices) =>
        prices
            .GroupBy(p => TruncateToHour(p.PeriodStartUtc))
            .OrderBy(g => g.Key)
            .Select(g => new HourlySpotPrice(g.Key, g.Average(p => p.DkkPerMwh)))
            .ToList();

    private static DateTimeOffset TruncateToHour(DateTimeOffset utc)
    {
        var u = utc.ToUniversalTime();
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
    }
}
