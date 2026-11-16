using System.Net;
using System.Net.Http.Json;
using Dapper;
using ElectricityDash.Web.Dashboard;
using Npgsql;

namespace ElectricityDash.Tests;

public class DashboardTests : IClassFixture<ApiFactory>
{
    private const string MeteringPoint = "571313100000000000"; // fake placeholder
    private readonly ApiFactory _factory;

    public DashboardTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Dashboard_combines_stored_prices_and_consumption_for_the_period()
    {
        var client = _factory.CreateClient(); // starts the host and runs the migrations
        // Clock is 2025-10-01 10:00 UTC. 2025-09-30 is a summer-time day: local midnight is 22:00 UTC the day before.
        var dayStart = new DateTimeOffset(2025, 9, 29, 22, 0, 0, TimeSpan.Zero);

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        for (var hour = 0; hour < 24; hour++)
        {
            await connection.ExecuteAsync(
                "INSERT INTO spot_price (price_area, hour_start, dkk_per_mwh) VALUES ('DK1', @HourStart, @Price)",
                new { HourStart = dayStart.AddHours(hour), Price = (hour + 1) * 100m }); // 0.1 .. 2.4 DKK/kWh
        }
        // A price in another area must never leak into the numbers.
        await connection.ExecuteAsync(
            "INSERT INTO spot_price (price_area, hour_start, dkk_per_mwh) VALUES ('DK2', @HourStart, 999999)",
            new { HourStart = dayStart });
        foreach (var hour in new[] { 0, 12 })
        {
            await connection.ExecuteAsync(
                "INSERT INTO consumption (metering_point_id, hour_start, kwh, quality) VALUES (@Meter, @HourStart, 2, 'A01')",
                new { Meter = MeteringPoint, HourStart = dayStart.AddHours(hour) });
        }

        var response = await client.GetAsync("/api/dashboard?period=last7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await response.Content.ReadFromJsonAsync<DashboardData>();
        Assert.NotNull(data);
        Assert.Equal("last7", data.Period.Key);
        Assert.Equal(new DateOnly(2025, 9, 25), data.Period.From);
        Assert.Equal(new DateOnly(2025, 10, 1), data.Period.To);
        Assert.Equal(7, data.Days.Count);

        var day = Assert.Single(data.Days, d => d.HasData);
        Assert.Equal(new DateOnly(2025, 9, 30), day.Date);
        Assert.Equal(4m, day.Kwh);
        Assert.Equal(0.5m, day.Score);
        Assert.Equal(0.7m, day.PaidDkkPerKwh);
        Assert.Equal(1.25m, day.MarketDkkPerKwh);
        Assert.Equal(0.5m, data.Totals.Score);
        Assert.Equal(1, data.Totals.DaysWithData);
        Assert.Equal(24, data.Hours.Count);
    }
}

/// <summary>Own class, so it gets its own (empty) database.</summary>
public class DashboardEmptyTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public DashboardEmptyTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Empty_database_gives_an_empty_dashboard_and_unknown_periods_fall_back_to_30_days()
    {
        var client = _factory.CreateClient();

        var data = await client.GetFromJsonAsync<DashboardData>("/api/dashboard?period=bogus");

        Assert.NotNull(data);
        Assert.Equal("last30", data.Period.Key);
        Assert.Equal(30, data.Days.Count);
        Assert.All(data.Days, d => Assert.False(d.HasData));
        Assert.Equal(0, data.Totals.DaysWithData);
        Assert.Null(data.Totals.Score);
        Assert.Null(data.Totals.PaidDkkPerKwh);
    }

    [Fact]
    public async Task Home_page_renders_the_dashboard_with_the_period_dropdown_and_the_spot_only_disclaimer()
    {
        var client = _factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("Last 30 days", html);
        Assert.Contains("Previous month", html);
        Assert.Contains("Spot price only", html);
    }
}
