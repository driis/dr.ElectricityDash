using System.Net;
using System.Net.Http.Json;
using System.Web;
using Dapper;
using ElectricityDash.Web.SpotPrices;
using Npgsql;

namespace ElectricityDash.Tests;

/// <summary>API-level tests: real app, real Postgres, Energi Data Service faked at the HTTP level.</summary>
public class IngestSpotPricesTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    // Dapper/Npgsql return timestamptz as a UTC DateTime.
    private record StoredPrice(string PriceArea, DateTime HourStart, decimal DkkPerMwh);

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        factory.EnergyDataService.Reset();
        factory.Time.Now = new DateTimeOffset(2025, 10, 1, 10, 0, 0, TimeSpan.Zero);
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.ExecuteAsync("TRUNCATE spot_price");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Quarter_hour_prices_are_stored_as_hourly_averages()
    {
        // 2025-10-01 in Denmark starts at 2025-09-30T22:00Z (UTC+2).
        factory.EnergyDataService.Responder = _ => FakeEnergyDataService.Json(
            FakeEnergyDataService.Quarters(new DateTime(2025, 9, 30, 22, 0, 0), "DK1",
                400, 500, 600, 700,   // hour avg 550
                100, 100, 100, 100)); // hour avg 100

        var response = await _client.PostAsync("/api/ingest/spot-prices?from=2025-10-01&to=2025-10-01", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.Equal(2, body?.HoursStored);
        Assert.Equal(
            [
                new StoredPrice("DK1", new DateTime(2025, 9, 30, 22, 0, 0, DateTimeKind.Utc), 550m),
                new StoredPrice("DK1", new DateTime(2025, 9, 30, 23, 0, 0, DateTimeKind.Utc), 100m)
            ],
            await StoredPrices());
    }

    [Fact]
    public async Task Requests_the_configured_price_area_for_the_danish_days_in_utc()
    {
        await _client.PostAsync("/api/ingest/spot-prices?from=2025-10-01&to=2025-10-02", null);

        var request = Assert.Single(factory.EnergyDataService.Requests);
        Assert.Equal("/dataset/DayAheadPrices", request.AbsolutePath);
        var query = HttpUtility.ParseQueryString(request.Query);
        Assert.Equal("2025-09-30T22:00", query["start"]);
        Assert.Equal("2025-10-02T22:00", query["end"]); // exclusive: start of 2025-10-03 in Denmark
        Assert.Equal("utc", query["timezone"]);
        Assert.Equal("""{"PriceArea":["DK1"]}""", query["filter"]);
    }

    [Fact]
    public async Task Ingesting_again_updates_existing_hours_instead_of_duplicating()
    {
        var start = new DateTime(2025, 9, 30, 22, 0, 0);
        factory.EnergyDataService.Responder = _ => FakeEnergyDataService.Json(
            FakeEnergyDataService.Quarters(start, "DK1", 100, 100, 100, 100));
        await _client.PostAsync("/api/ingest/spot-prices?from=2025-10-01&to=2025-10-01", null);

        factory.EnergyDataService.Responder = _ => FakeEnergyDataService.Json(
            FakeEnergyDataService.Quarters(start, "DK1", 200, 200, 200, 200));
        var response = await _client.PostAsync("/api/ingest/spot-prices?from=2025-10-01&to=2025-10-01", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = Assert.Single(await StoredPrices());
        Assert.Equal(200m, stored.DkkPerMwh);
    }

    [Fact]
    public async Task Defaults_to_today_and_tomorrow_in_danish_time()
    {
        // 23:30Z on 2025-10-01 is already 01:30 on 2025-10-02 in Denmark.
        factory.Time.Now = new DateTimeOffset(2025, 10, 1, 23, 30, 0, TimeSpan.Zero);

        var response = await _client.PostAsync("/api/ingest/spot-prices", null);

        var body = await response.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.Equal(new DateOnly(2025, 10, 2), body?.From);
        Assert.Equal(new DateOnly(2025, 10, 3), body?.To);
    }

    [Fact]
    public async Task No_published_prices_is_not_an_error()
    {
        var response = await _client.PostAsync("/api/ingest/spot-prices?from=2030-01-01&to=2030-01-01", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<IngestResponse>())?.HoursStored);
        Assert.Empty(await StoredPrices());
    }

    [Fact]
    public async Task Upstream_failure_gives_bad_gateway_and_stores_nothing()
    {
        factory.EnergyDataService.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var response = await _client.PostAsync("/api/ingest/spot-prices?from=2025-10-01&to=2025-10-01", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await StoredPrices());
    }

    [Fact]
    public async Task Rate_limited_calls_wait_for_retry_after_and_then_succeed()
    {
        var calls = 0;
        factory.EnergyDataService.Responder = _ =>
        {
            if (++calls > 1)
            {
                return FakeEnergyDataService.Json(
                    FakeEnergyDataService.Quarters(new DateTime(2025, 9, 30, 22, 0, 0), "DK1", 100, 100, 100, 100));
            }

            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return limited;
        };

        var started = System.Diagnostics.Stopwatch.StartNew();
        var response = await _client.PostAsync("/api/ingest/spot-prices?from=2025-10-01&to=2025-10-01", null);
        started.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, factory.EnergyDataService.Requests.Count);
        // The test factory configures a 1 ms retry delay, so waiting about a second proves Retry-After was honoured.
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(900), $"Only waited {started.Elapsed}");
        Assert.Single(await StoredPrices());
    }

    [Fact]
    public async Task A_missing_price_fails_the_whole_ingest_rather_than_leaving_a_gap()
    {
        factory.EnergyDataService.Responder = _ => FakeEnergyDataService.Json(
            FakeEnergyDataService.Quarters(new DateTime(2025, 9, 30, 22, 0, 0), "DK1", 100, null, 100, 100));

        var response = await _client.PostAsync("/api/ingest/spot-prices?from=2025-10-01&to=2025-10-01", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await StoredPrices());
    }

    [Theory]
    [InlineData("from=2025-10-02&to=2025-10-01")]
    [InlineData("from=2024-01-01&to=2025-10-01")] // more than 366 days
    public async Task Invalid_ranges_are_rejected_without_calling_upstream(string query)
    {
        var response = await _client.PostAsync($"/api/ingest/spot-prices?{query}", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.EnergyDataService.Requests);
    }

    private async Task<List<StoredPrice>> StoredPrices()
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        var rows = await connection.QueryAsync<StoredPrice>(
            "SELECT price_area AS PriceArea, hour_start AS HourStart, dkk_per_mwh AS DkkPerMwh FROM spot_price ORDER BY hour_start");
        return rows.ToList();
    }
}
