namespace ElectricityDash.Web.Dashboard;

public static class Periods
{
    public const string DefaultKey = "last30";

    public static readonly IReadOnlyList<(string Key, string Label)> Options =
    [
        ("last7", "Last 7 days"),
        ("last14", "Last 14 days"),
        ("last30", "Last 30 days"),
        ("last90", "Last 90 days"),
        ("this-month", "This month"),
        ("last-month", "Previous month"),
    ];

    /// <summary>Resolves a period key relative to <paramref name="today"/> (a Danish calendar day). Unknown keys fall back to the default.</summary>
    public static Period Resolve(string? key, DateOnly today)
    {
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);
        var (resolvedKey, from, to) = key switch
        {
            "last7" => ("last7", today.AddDays(-6), today),
            "last14" => ("last14", today.AddDays(-13), today),
            "last90" => ("last90", today.AddDays(-89), today),
            "this-month" => ("this-month", firstOfMonth, today),
            "last-month" => ("last-month", firstOfMonth.AddMonths(-1), firstOfMonth.AddDays(-1)),
            _ => (DefaultKey, today.AddDays(-29), today),
        };
        var label = Options.First(o => o.Key == resolvedKey).Label;
        return new Period(resolvedKey, label, from, to);
    }
}
