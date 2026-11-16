using ElectricityDash.Web.Components.Dashboard;

namespace ElectricityDash.Tests;

public class ChartScaleTests
{
    [Fact]
    public void Positive_data_gets_a_zero_based_axis_with_nice_ticks()
    {
        var axis = ChartScale.Nice(0, 17.3);

        Assert.Equal(0, axis.Min);
        Assert.Equal(20, axis.Max);
        Assert.Equal([0.0, 5.0, 10.0, 15.0, 20.0], axis.Ticks);
        Assert.Equal(0.5, axis.Fraction(10));
    }

    [Fact]
    public void Negative_spot_prices_extend_the_axis_below_zero_and_zero_stays_a_tick()
    {
        var axis = ChartScale.Nice(-0.3, 1.2);

        Assert.True(axis.Min <= -0.3);
        Assert.True(axis.Max >= 1.2);
        Assert.Contains(0.0, axis.Ticks.Select(t => Math.Round(t, 10)));
        Assert.InRange(axis.Fraction(-0.3), 0, 1);
    }

    [Fact]
    public void No_data_gives_a_usable_axis()
    {
        var axis = ChartScale.Nice(0, 0);

        Assert.Equal(0, axis.Min);
        Assert.Equal(1, axis.Max);
    }
}
