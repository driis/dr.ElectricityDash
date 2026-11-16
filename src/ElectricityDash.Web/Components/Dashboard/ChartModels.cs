namespace ElectricityDash.Web.Components.Dashboard;

/// <summary>One row in a hover tooltip. <paramref name="SwatchClass"/> draws the series colour next to the label.</summary>
public sealed record TipRow(string Label, string Value, string? SwatchClass = null);

public sealed record BarSegment(double Value, string CssClass);

public sealed record BarColumn(string AxisLabel, string TipTitle, IReadOnlyList<BarSegment> Segments, IReadOnlyList<TipRow> Tip)
{
    public double Total => Segments.Sum(s => s.Value);
}

/// <summary>A line series. Null values are gaps (no data), never zero.</summary>
public sealed record LineSeries(string Name, string CssClass, IReadOnlyList<double?> Values, bool Dashed = false);

/// <summary>An axis from <paramref name="Min"/> to <paramref name="Max"/> with evenly spaced <paramref name="Ticks"/>.</summary>
public sealed record AxisScale(double Min, double Max, IReadOnlyList<double> Ticks)
{
    /// <summary>Position of <paramref name="value"/> as a 0-1 fraction of the axis height, bottom to top.</summary>
    public double Fraction(double value) => (value - Min) / (Max - Min);
}

public static class ChartScale
{
    /// <summary>
    /// A "nice" axis covering <paramref name="min"/>..<paramref name="max"/> and always including zero.
    /// Spot prices can go negative, so lines need a floor below zero.
    /// </summary>
    public static AxisScale Nice(double min, double max, int targetTicks = 4)
    {
        min = Math.Min(min, 0);
        max = Math.Max(max, 0);
        if (max - min <= 0) return new AxisScale(0, 1, [0, 1]);

        var rough = (max - min) / targetTicks;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var step = new[] { 1.0, 2.0, 2.5, 5.0, 10.0 }.Select(m => m * magnitude).First(s => s >= rough - 1e-12);

        var niceMin = Math.Floor(min / step) * step;
        var niceMax = Math.Ceiling(max / step) * step;
        var ticks = Enumerable.Range(0, (int)Math.Round((niceMax - niceMin) / step) + 1).Select(i => niceMin + i * step).ToList();
        return new AxisScale(niceMin, niceMax, ticks);
    }
}
