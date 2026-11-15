using ElectricityDash.Web.SpotPrices;

namespace ElectricityDash.Tests;

public class HourlyAggregationTests
{
    private static DateTimeOffset Utc(int day, int hour, int minute = 0) =>
        new(2025, 10, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Averages_quarters_into_the_hour_they_start_in()
    {
        var result = HourlyAggregation.ToHourly(
        [
            new(Utc(1, 0, 0), 100), new(Utc(1, 0, 15), 200), new(Utc(1, 0, 30), 300), new(Utc(1, 0, 45), 400),
            new(Utc(1, 1, 0), 50)
        ]);

        Assert.Equal([new HourlySpotPrice(Utc(1, 0), 250m), new HourlySpotPrice(Utc(1, 1), 50m)], result);
    }

    [Fact]
    public void Hourly_input_passes_through_unchanged_and_sorted()
    {
        var result = HourlyAggregation.ToHourly([new(Utc(1, 5), 5), new(Utc(1, 4), 4)]);

        Assert.Equal([new HourlySpotPrice(Utc(1, 4), 4m), new HourlySpotPrice(Utc(1, 5), 5m)], result);
    }

    [Fact]
    public void Empty_input_gives_empty_output() =>
        Assert.Empty(HourlyAggregation.ToHourly([]));
}

public class DanishDaysToUtcTests
{
    [Fact]
    public void Summer_time_day_starts_two_hours_before_midnight_utc()
    {
        var (start, end) = SpotPriceIngestor.DanishDaysToUtc(new DateOnly(2025, 7, 1), new DateOnly(2025, 7, 1));

        Assert.Equal(new DateTimeOffset(2025, 6, 30, 22, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2025, 7, 1, 22, 0, 0, TimeSpan.Zero), end);
    }

    [Fact]
    public void Winter_time_day_starts_one_hour_before_midnight_utc()
    {
        var (start, _) = SpotPriceIngestor.DanishDaysToUtc(new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 1));

        Assert.Equal(new DateTimeOffset(2025, 11, 30, 23, 0, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void Day_the_clocks_go_back_is_25_hours()
    {
        var (start, end) = SpotPriceIngestor.DanishDaysToUtc(new DateOnly(2025, 10, 26), new DateOnly(2025, 10, 26));

        Assert.Equal(TimeSpan.FromHours(25), end - start);
    }

    [Fact]
    public void Day_the_clocks_go_forward_is_23_hours()
    {
        var (start, end) = SpotPriceIngestor.DanishDaysToUtc(new DateOnly(2026, 3, 29), new DateOnly(2026, 3, 29));

        Assert.Equal(TimeSpan.FromHours(23), end - start);
    }

    [Fact]
    public void Danish_date_is_taken_in_local_time()
    {
        // 23:30Z in summer is 01:30 the next day in Denmark.
        var date = SpotPriceIngestor.DanishDate(new DateTimeOffset(2025, 7, 1, 23, 30, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(2025, 7, 2), date);
    }
}
