namespace ElectricityDash.Web.Dashboard;

/// <summary>
/// Pure calculation of the dashboard numbers from hourly prices and hourly consumption.
/// "Day" is the Danish calendar day, so DST days have 23 or 25 hours.
/// Off-peak = the 6 cheapest hours of a day, ties broken by earlier hour (see CLAUDE.md).
/// </summary>
public static class DashboardCalculator
{
    public const int OffPeakHoursPerDay = 6;

    private static readonly TimeZoneInfo Copenhagen = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");

    public static DateOnly LocalDate(DateTime utc) => DateOnly.FromDateTime(ToLocal(utc));

    /// <summary>UTC instant of local midnight at the start of <paramref name="date"/>.</summary>
    public static DateTime StartOfDayUtc(DateOnly date) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), Copenhagen);

    public static DashboardData Calculate(Period period, IReadOnlyList<HourlyPrice> prices, IReadOnlyList<HourlyUsage> usage)
    {
        var pricesByDay = prices.ToLookup(p => LocalDate(p.HourStartUtc));
        var usageByDay = usage.ToLookup(u => LocalDate(u.HourStartUtc));

        var days = new List<DayStats>(period.DayCount);
        var offPeakHoursByDay = new Dictionary<DateOnly, HashSet<DateTime>>();

        for (var date = period.From; date <= period.To; date = date.AddDays(1))
        {
            var dayPrices = pricesByDay[date].ToList();
            var dayUsage = usageByDay[date].ToList();

            var expectedHours = (int)(StartOfDayUtc(date.AddDays(1)) - StartOfDayUtc(date)).TotalHours;
            var complete = dayPrices.Count == expectedHours;
            if (complete)
            {
                offPeakHoursByDay[date] = dayPrices
                    .OrderBy(p => p.DkkPerKwh).ThenBy(p => p.HourStartUtc)
                    .Take(OffPeakHoursPerDay)
                    .Select(p => p.HourStartUtc)
                    .ToHashSet();
            }

            days.Add(BuildDay(date, dayPrices, dayUsage, complete ? offPeakHoursByDay[date] : null));
        }

        var daysWithData = days.Where(d => d.HasData).ToList();
        return new DashboardData(period, days, BuildHours(daysWithData, pricesByDay, usageByDay, offPeakHoursByDay), BuildTotals(daysWithData, pricesByDay, usageByDay));
    }

    private static DayStats BuildDay(
        DateOnly date, List<HourlyPrice> dayPrices, List<HourlyUsage> dayUsage, HashSet<DateTime>? offPeakHours)
    {
        var priceByHour = dayPrices.ToDictionary(p => p.HourStartUtc, p => p.DkkPerKwh);
        var kwh = dayUsage.Sum(u => u.Kwh);

        var offPeakKwh = offPeakHours is null ? 0 : dayUsage.Where(u => offPeakHours.Contains(u.HourStartUtc)).Sum(u => u.Kwh);

        var priced = dayUsage.Where(u => priceByHour.ContainsKey(u.HourStartUtc)).ToList();
        var pricedKwh = priced.Sum(u => u.Kwh);
        decimal? paid = pricedKwh > 0 ? priced.Sum(u => u.Kwh * priceByHour[u.HourStartUtc]) / pricedKwh : null;
        decimal? market = dayPrices.Count > 0 ? dayPrices.Average(p => p.DkkPerKwh) : null;

        return new DayStats(date, dayUsage.Count > 0, kwh, offPeakKwh, offPeakHours is null ? 0 : kwh, paid, market);
    }

    private static PeriodTotals BuildTotals(
        List<DayStats> daysWithData, ILookup<DateOnly, HourlyPrice> pricesByDay, ILookup<DateOnly, HourlyUsage> usageByDay)
    {
        var kwh = daysWithData.Sum(d => d.Kwh);
        var offPeak = daysWithData.Sum(d => d.OffPeakKwh);
        var scored = daysWithData.Sum(d => d.ScoredKwh);

        // Paid price is weighted by kWh over every hour that has both consumption and a price.
        decimal cost = 0, pricedKwh = 0;
        foreach (var day in daysWithData)
        {
            var priceByHour = pricesByDay[day.Date].ToDictionary(p => p.HourStartUtc, p => p.DkkPerKwh);
            foreach (var u in usageByDay[day.Date].Where(u => priceByHour.ContainsKey(u.HourStartUtc)))
            {
                cost += u.Kwh * priceByHour[u.HourStartUtc];
                pricedKwh += u.Kwh;
            }
        }

        // Market average only over days we have consumption for, so the comparison is like for like.
        var marketPrices = daysWithData.SelectMany(d => pricesByDay[d.Date]).ToList();

        return new PeriodTotals(
            daysWithData.Count, kwh, offPeak, scored,
            scored > 0 ? offPeak / scored : null,
            pricedKwh > 0 ? cost / pricedKwh : null,
            marketPrices.Count > 0 ? marketPrices.Average(p => p.DkkPerKwh) : null);
    }

    private static List<HourOfDayStats> BuildHours(
        List<DayStats> daysWithData,
        ILookup<DateOnly, HourlyPrice> pricesByDay,
        ILookup<DateOnly, HourlyUsage> usageByDay,
        Dictionary<DateOnly, HashSet<DateTime>> offPeakHoursByDay)
    {
        var usage = daysWithData.SelectMany(d => usageByDay[d.Date]).ToLookup(u => ToLocal(u.HourStartUtc).Hour);
        var prices = daysWithData.SelectMany(d => pricesByDay[d.Date]).ToLookup(p => ToLocal(p.HourStartUtc).Hour);
        var scorableDays = daysWithData.Where(d => offPeakHoursByDay.ContainsKey(d.Date)).ToList();

        return Enumerable.Range(0, 24).Select(hour =>
        {
            var hourUsage = usage[hour].ToList();
            var hourPrices = prices[hour].ToList();
            var cheapDays = scorableDays.Count(d => offPeakHoursByDay[d.Date].Any(h => ToLocal(h).Hour == hour));
            return new HourOfDayStats(
                hour,
                hourUsage.Count > 0 ? hourUsage.Average(u => u.Kwh) : null,
                hourPrices.Count > 0 ? hourPrices.Average(p => p.DkkPerKwh) : null,
                scorableDays.Count > 0 ? (decimal)cheapDays / scorableDays.Count : null);
        }).ToList();
    }

    private static DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Copenhagen);
}
