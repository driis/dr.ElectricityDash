using ElectricityDash.Web.Dashboard;

namespace ElectricityDash.Tests;

public class DashboardCalculatorTests
{
    private static readonly TimeZoneInfo Copenhagen = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");

    private static DateTime Utc(DateOnly localDay, int localHourIndex) =>
        DashboardCalculator.StartOfDayUtc(localDay).AddHours(localHourIndex);

    private static int HoursIn(DateOnly day) =>
        (int)(DashboardCalculator.StartOfDayUtc(day.AddDays(1)) - DashboardCalculator.StartOfDayUtc(day)).TotalHours;

    /// <summary>One price per hour of the local day. <paramref name="dkkPerKwh"/> maps the hour index (0-based, in UTC order) to a price.</summary>
    private static List<HourlyPrice> Prices(DateOnly day, Func<int, decimal> dkkPerKwh) =>
        Enumerable.Range(0, HoursIn(day)).Select(i => new HourlyPrice(Utc(day, i), dkkPerKwh(i))).ToList();

    private static Period OneDay(DateOnly day) => new("test", "Test", day, day);

    [Fact]
    public void Off_peak_is_the_six_cheapest_hours_and_score_is_their_share_of_kwh()
    {
        var day = new DateOnly(2025, 9, 30);
        var prices = Prices(day, i => (i + 1) / 10m); // hour 0 cheapest, hour 23 dearest
        List<HourlyUsage> usage = [new(Utc(day, 0), 2m), new(Utc(day, 12), 2m)];

        var data = DashboardCalculator.Calculate(OneDay(day), prices, usage);

        var stats = Assert.Single(data.Days);
        Assert.Equal(4m, stats.Kwh);
        Assert.Equal(2m, stats.OffPeakKwh);
        Assert.Equal(0.5m, stats.Score);
        Assert.Equal(0.5m, data.Totals.Score);
        Assert.Equal((2m * 0.1m + 2m * 1.3m) / 4m, stats.PaidDkkPerKwh); // weighted by kWh
        Assert.Equal(1.25m, stats.MarketDkkPerKwh);                      // plain average of 0.1..2.4
        Assert.Equal(0.7m / 1.25m - 1, data.Totals.PaidVersusMarket);    // 44% cheaper than market
    }

    [Fact]
    public void Ties_are_broken_by_earlier_hour()
    {
        var day = new DateOnly(2025, 9, 30);
        var prices = Prices(day, _ => 1m); // every hour costs the same
        List<HourlyUsage> usage = [new(Utc(day, 5), 1m), new(Utc(day, 6), 1m)];

        var stats = Assert.Single(DashboardCalculator.Calculate(OneDay(day), prices, usage).Days);

        Assert.Equal(1m, stats.OffPeakKwh); // hours 0-5 are off-peak, hour 6 is not
        Assert.Equal(0.5m, stats.Score);
    }

    [Theory]
    [InlineData(2025, 10, 26, 25)] // clocks go back
    [InlineData(2026, 3, 29, 23)]  // clocks go forward
    [InlineData(2025, 6, 15, 24)]
    public void Dst_days_have_23_or_25_hours_and_still_score_the_six_cheapest(int year, int month, int dayOfMonth, int hours)
    {
        var day = new DateOnly(year, month, dayOfMonth);
        Assert.Equal(hours, HoursIn(day));

        var prices = Prices(day, i => i + 1m);
        var usage = Enumerable.Range(0, hours).Select(i => new HourlyUsage(Utc(day, i), 1m)).ToList();

        var stats = Assert.Single(DashboardCalculator.Calculate(OneDay(day), prices, usage).Days);

        Assert.Equal(hours, stats.Kwh);
        Assert.Equal(6m, stats.OffPeakKwh);
        Assert.Equal(6m / hours, stats.Score);
    }

    [Fact]
    public void Day_without_a_complete_set_of_prices_is_not_scored()
    {
        var day = new DateOnly(2025, 9, 30);
        var prices = Prices(day, i => i + 1m).Take(20).ToList(); // prices missing for the last hours
        List<HourlyUsage> usage = [new(Utc(day, 0), 3m)];

        var data = DashboardCalculator.Calculate(OneDay(day), prices, usage);

        var stats = Assert.Single(data.Days);
        Assert.True(stats.HasData);
        Assert.Equal(3m, stats.Kwh);
        Assert.Equal(3m, stats.UnscoredKwh);
        Assert.Null(stats.Score);
        Assert.Null(data.Totals.Score);
        Assert.Equal(1m, stats.PaidDkkPerKwh); // the hour that does have a price still counts towards the paid price
    }

    [Fact]
    public void Days_without_consumption_are_present_but_empty_and_excluded_from_totals()
    {
        var empty = new DateOnly(2025, 9, 29);
        var withData = new DateOnly(2025, 9, 30);
        var prices = Prices(empty, _ => 9m).Concat(Prices(withData, _ => 1m)).ToList();
        List<HourlyUsage> usage = [new(Utc(withData, 0), 1m)];

        var data = DashboardCalculator.Calculate(new Period("test", "Test", empty, withData), prices, usage);

        Assert.Equal(2, data.Days.Count);
        Assert.False(data.Days[0].HasData);
        Assert.Null(data.Days[0].Score);
        Assert.Equal(1, data.Totals.DaysWithData);
        Assert.Equal(1m, data.Totals.MarketDkkPerKwh); // the expensive empty day does not skew the market average
    }

    [Fact]
    public void Hour_of_day_profile_uses_local_hours_and_counts_how_often_an_hour_is_cheap()
    {
        var day1 = new DateOnly(2025, 9, 29);
        var day2 = new DateOnly(2025, 9, 30);
        // Local hour 3 is the cheapest on both days, local hour 20 is the dearest.
        var prices = Prices(day1, i => i == 3 ? 0.1m : i == 20 ? 5m : 1m)
            .Concat(Prices(day2, i => i == 3 ? 0.1m : i == 20 ? 5m : 1m)).ToList();
        List<HourlyUsage> usage = [new(Utc(day1, 3), 1m), new(Utc(day2, 3), 3m)];

        var data = DashboardCalculator.Calculate(new Period("test", "Test", day1, day2), prices, usage);

        var hour3 = data.Hours[3];
        Assert.Equal(2m, hour3.AvgKwh);
        Assert.Equal(0.1m, hour3.AvgDkkPerKwh);
        Assert.Equal(1m, hour3.CheapShare);
        Assert.Null(data.Hours[10].AvgKwh);
        Assert.Equal(0m, data.Hours[20].CheapShare);
        Assert.Equal(Copenhagen.GetUtcOffset(Utc(day1, 0)), TimeSpan.FromHours(2)); // sanity: tests run in summer time
    }

    [Fact]
    public void Periods_resolve_relative_to_the_danish_day()
    {
        var today = new DateOnly(2025, 10, 15);

        var last30 = Periods.Resolve("last30", today);
        Assert.Equal(new DateOnly(2025, 9, 16), last30.From);
        Assert.Equal(today, last30.To);
        Assert.Equal(30, last30.DayCount);

        var thisMonth = Periods.Resolve("this-month", today);
        Assert.Equal(new DateOnly(2025, 10, 1), thisMonth.From);

        var lastMonth = Periods.Resolve("last-month", today);
        Assert.Equal(new DateOnly(2025, 9, 1), lastMonth.From);
        Assert.Equal(new DateOnly(2025, 9, 30), lastMonth.To);

        Assert.Equal("last30", Periods.Resolve("nonsense", today).Key);
        Assert.Equal("last30", Periods.Resolve(null, today).Key);
    }
}
