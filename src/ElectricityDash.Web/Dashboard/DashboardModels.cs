namespace ElectricityDash.Web.Dashboard;

/// <summary>Spot price for one delivery hour, DKK per kWh excluding VAT.</summary>
public sealed record HourlyPrice(DateTime HourStartUtc, decimal DkkPerKwh);

/// <summary>Metered consumption for one delivery hour.</summary>
public sealed record HourlyUsage(DateTime HourStartUtc, decimal Kwh);

/// <summary>A selectable dashboard period. <see cref="From"/> and <see cref="To"/> are Danish calendar days, inclusive.</summary>
public sealed record Period(string Key, string Label, DateOnly From, DateOnly To)
{
    public int DayCount => To.DayNumber - From.DayNumber + 1;
}

/// <summary>Everything the dashboard shows for one Danish calendar day.</summary>
/// <param name="Kwh">Total consumption delivered so far. Zero when <see cref="HasData"/> is false.</param>
/// <param name="OffPeakKwh">kWh used in the 6 cheapest hours. Only counted when the whole day has prices.</param>
/// <param name="ScoredKwh">kWh that could be scored (all of <see cref="Kwh"/> on a day with complete prices, otherwise zero).</param>
/// <param name="PaidDkkPerKwh">Consumption-weighted spot price, over hours that have both consumption and a price.</param>
/// <param name="MarketDkkPerKwh">Plain average of the day's hourly spot prices.</param>
public sealed record DayStats(
    DateOnly Date,
    bool HasData,
    decimal Kwh,
    decimal OffPeakKwh,
    decimal ScoredKwh,
    decimal? PaidDkkPerKwh,
    decimal? MarketDkkPerKwh)
{
    public decimal PeakKwh => ScoredKwh - OffPeakKwh;

    /// <summary>kWh that arrived without a complete day of prices, so it cannot be scored.</summary>
    public decimal UnscoredKwh => Kwh - ScoredKwh;

    /// <summary>Share (0-1) of the day's kWh used in off-peak hours, or null when there is nothing to score.</summary>
    public decimal? Score => ScoredKwh > 0 ? OffPeakKwh / ScoredKwh : null;
}

/// <summary>Average behaviour for one local hour of day (0-23) across the period.</summary>
/// <param name="CheapShare">Share (0-1) of scorable days on which this hour was one of the 6 cheapest.</param>
public sealed record HourOfDayStats(int Hour, decimal? AvgKwh, decimal? AvgDkkPerKwh, decimal? CheapShare);

/// <param name="Score">Off-peak share (0-1) of the kWh that could be scored.</param>
/// <param name="PaidDkkPerKwh">Consumption-weighted spot price: what the used kWh cost on average.</param>
/// <param name="MarketDkkPerKwh">Plain average spot price over the days that have consumption.</param>
public sealed record PeriodTotals(
    int DaysWithData,
    decimal Kwh,
    decimal OffPeakKwh,
    decimal ScoredKwh,
    decimal? Score,
    decimal? PaidDkkPerKwh,
    decimal? MarketDkkPerKwh)
{
    /// <summary>Paid price relative to the market average, e.g. -0.12 = 12% cheaper than average. Null when either is unknown.</summary>
    public decimal? PaidVersusMarket =>
        PaidDkkPerKwh is { } paid && MarketDkkPerKwh is { } market and > 0 ? paid / market - 1 : null;
}

public sealed record DashboardData(
    Period Period,
    IReadOnlyList<DayStats> Days,
    IReadOnlyList<HourOfDayStats> Hours,
    PeriodTotals Totals)
{
    /// <summary>Share of kWh that a perfectly flat usage pattern would put in off-peak hours (6 of 24).</summary>
    public const decimal EvenUseScore = 0.25m;

    /// <summary>What the dashboard prices cover. Shown in the UI and API so a total is never mistaken for a full bill.</summary>
    public string PriceScope { get; } =
        "Spot price only, excluding VAT. Grid and system tariffs, electricity tax, supplier margin and the subscription fee are not included.";
}
