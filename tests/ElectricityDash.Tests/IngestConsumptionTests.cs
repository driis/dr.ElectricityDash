using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using ElectricityDash.Web.Eloverblik;
using Npgsql;

namespace ElectricityDash.Tests;

/// <summary>API-level tests: real app, real Postgres, Eloverblik faked at the HTTP level.</summary>
public class IngestConsumptionTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    // Dapper/Npgsql return timestamptz as a UTC DateTime.
    private record StoredConsumption(string MeteringPointId, DateTime HourStart, decimal Kwh, string Quality);

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        factory.Eloverblik.Reset();
        factory.Time.Now = new DateTimeOffset(2025, 10, 1, 10, 0, 0, TimeSpan.Zero);
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.ExecuteAsync("TRUNCATE consumption");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static DateTime Utc(int year, int month, int day, int hour = 0) =>
        new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Hourly_points_are_stored_per_utc_hour_for_the_discovered_meter()
    {
        // 2025-09-30 in Denmark starts at 2025-09-29T22:00Z (UTC+2).
        factory.Eloverblik.TimeSeries = () => FakeEloverblik.TimeSeriesJson(
            FakeEloverblik.HourlyPeriod(Utc(2025, 9, 29, 22), 0.5m, 1.25m, 0.000m));

        var response = await _client.PostAsync("/api/ingest/consumption?from=2025-09-30&to=2025-09-30", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsumptionIngestResponse>();
        Assert.Equal(3, body?.HoursStored);
        Assert.Equal(
            [
                new StoredConsumption(FakeEloverblik.MeteringPointId, Utc(2025, 9, 29, 22), 0.5m, "A04"),
                new StoredConsumption(FakeEloverblik.MeteringPointId, Utc(2025, 9, 29, 23), 1.25m, "A04"),
                new StoredConsumption(FakeEloverblik.MeteringPointId, Utc(2025, 9, 30, 0), 0m, "A04")
            ],
            await Stored());
    }

    [Fact]
    public async Task Exchanges_the_refresh_token_and_requests_hourly_data_with_an_exclusive_end_date()
    {
        await _client.PostAsync("/api/ingest/consumption?from=2025-09-29&to=2025-09-30", null);

        var token = factory.Eloverblik.Requests[0];
        Assert.Equal(HttpMethod.Get, token.Method);
        Assert.Equal("/customerapi/api/token", token.Uri.AbsolutePath);
        Assert.Equal(FakeEloverblik.RefreshToken, token.Authorization);

        var series = Assert.Single(factory.Eloverblik.TimeSeriesRequests);
        Assert.Equal(HttpMethod.Post, series.Method);
        // Our range is inclusive, Eloverblik's dateTo is exclusive.
        Assert.Equal("/customerapi/api/meterdata/gettimeseries/2025-09-29/2025-10-01/Hour", series.Uri.AbsolutePath);
        Assert.Equal(FakeEloverblik.AccessToken, series.Authorization);
        Assert.Equal(
            FakeEloverblik.MeteringPointId,
            JsonDocument.Parse(series.Body!).RootElement.GetProperty("meteringPoints").GetProperty("meteringPoint")[0].GetString());

        // One token exchange per ingest, shared by the metering point lookup and the time series call.
        Assert.Single(factory.Eloverblik.Requests, r => r.Uri.AbsolutePath.EndsWith("/token"));
    }

    [Fact]
    public async Task Ingesting_again_updates_revised_values_and_quality_instead_of_duplicating()
    {
        factory.Eloverblik.TimeSeries = () => FakeEloverblik.TimeSeriesJson(
            FakeEloverblik.Period(Utc(2025, 9, 29, 22), "PT1H", "A03", 1m)); // estimated
        await _client.PostAsync("/api/ingest/consumption?from=2025-09-30&to=2025-09-30", null);

        factory.Eloverblik.TimeSeries = () => FakeEloverblik.TimeSeriesJson(
            FakeEloverblik.Period(Utc(2025, 9, 29, 22), "PT1H", "A04", 1.5m)); // real reading arrived
        var response = await _client.PostAsync("/api/ingest/consumption?from=2025-09-30&to=2025-09-30", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = Assert.Single(await Stored());
        Assert.Equal(1.5m, stored.Kwh);
        Assert.Equal("A04", stored.Quality);
    }

    [Fact]
    public async Task A_25_hour_day_stores_25_distinct_hours()
    {
        // 2025-10-26 is the DST fall-back day: starts 2025-10-25T22:00Z, ends 2025-10-26T23:00Z.
        factory.Eloverblik.TimeSeries = () => FakeEloverblik.TimeSeriesJson(
            FakeEloverblik.HourlyPeriod(Utc(2025, 10, 25, 22), Enumerable.Repeat((decimal?)0.2m, 25).ToArray()));

        var response = await _client.PostAsync("/api/ingest/consumption?from=2025-10-26&to=2025-10-26", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await Stored();
        Assert.Equal(25, stored.Count);
        Assert.Equal(Utc(2025, 10, 25, 22), stored[0].HourStart);
        Assert.Equal(Utc(2025, 10, 26, 22), stored[^1].HourStart);
    }

    [Fact]
    public async Task A_23_hour_day_stores_23_hours()
    {
        // 2025-03-30 is the DST spring-forward day: starts 2025-03-29T23:00Z, ends 2025-03-30T22:00Z.
        factory.Eloverblik.TimeSeries = () => FakeEloverblik.TimeSeriesJson(
            FakeEloverblik.HourlyPeriod(Utc(2025, 3, 29, 23), Enumerable.Repeat((decimal?)0.2m, 23).ToArray()));

        await _client.PostAsync("/api/ingest/consumption?from=2025-03-30&to=2025-03-30", null);

        var stored = await Stored();
        Assert.Equal(23, stored.Count);
        Assert.Equal(Utc(2025, 3, 30, 21), stored[^1].HourStart);
    }

    [Fact]
    public async Task Hours_not_delivered_yet_are_left_out_rather_than_stored_as_zero()
    {
        factory.Eloverblik.TimeSeries = () => FakeEloverblik.TimeSeriesJson(
            FakeEloverblik.HourlyPeriod(Utc(2025, 9, 29, 22), 1m, null, 3m));

        var response = await _client.PostAsync("/api/ingest/consumption?from=2025-09-30&to=2025-09-30", null);

        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<ConsumptionIngestResponse>())?.HoursStored);
        Assert.Equal([Utc(2025, 9, 29, 22), Utc(2025, 9, 30, 0)], (await Stored()).Select(s => s.HourStart));
    }

    [Fact]
    public async Task No_data_yet_is_not_an_error()
    {
        var response = await _client.PostAsync("/api/ingest/consumption?from=2025-09-30&to=2025-09-30", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<ConsumptionIngestResponse>())?.HoursStored);
        Assert.Empty(await Stored());
    }

    [Fact]
    public async Task Defaults_to_the_last_week_up_to_today_in_danish_time()
    {
        // 23:30Z on 2025-10-01 is already 01:30 on 2025-10-02 in Denmark.
        factory.Time.Now = new DateTimeOffset(2025, 10, 1, 23, 30, 0, TimeSpan.Zero);

        var response = await _client.PostAsync("/api/ingest/consumption", null);

        var body = await response.Content.ReadFromJsonAsync<ConsumptionIngestResponse>();
        Assert.Equal(new DateOnly(2025, 9, 25), body?.From);
        Assert.Equal(new DateOnly(2025, 10, 2), body?.To);
    }

    [Fact]
    public async Task Upstream_failure_gives_bad_gateway_and_stores_nothing()
    {
        factory.Eloverblik.TimeSeries = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var response = await _client.PostAsync("/api/ingest/consumption?from=2025-09-30&to=2025-09-30", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await Stored());
    }

    [Fact]
    public async Task An_error_reported_in_a_successful_response_gives_bad_gateway()
    {
        factory.Eloverblik.TimeSeries = () => FakeEloverblik.ErrorJson(20000, "Something went wrong");

        var response = await _client.PostAsync("/api/ingest/consumption?from=2025-09-30&to=2025-09-30", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("20000", await response.Content.ReadAsStringAsync());
        Assert.Empty(await Stored());
    }

    [Fact]
    public async Task Unexpected_resolution_fails_the_ingest_rather_than_misplacing_hours()
    {
        factory.Eloverblik.TimeSeries = () => FakeEloverblik.TimeSeriesJson(
            FakeEloverblik.Period(Utc(2025, 9, 29, 22), "PT15M", "A04", 1m, 1m, 1m, 1m));

        var response = await _client.PostAsync("/api/ingest/consumption?from=2025-09-30&to=2025-09-30", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await Stored());
    }

    [Fact]
    public async Task Unexpected_unit_fails_the_ingest()
    {
        factory.Eloverblik.TimeSeries = () => FakeEloverblik.TimeSeriesJson(
            "MWH", FakeEloverblik.HourlyPeriod(Utc(2025, 9, 29, 22), 1m));

        var response = await _client.PostAsync("/api/ingest/consumption?from=2025-09-30&to=2025-09-30", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await Stored());
    }

    [Fact]
    public async Task Several_consumption_meters_without_a_configured_one_is_an_error()
    {
        factory.Eloverblik.MeteringPoints = () => FakeEloverblik.MeteringPointsJson(
            ("571313100000000001", "E17"), ("571313100000000002", "E17"));

        var response = await _client.PostAsync("/api/ingest/consumption?from=2025-09-30&to=2025-09-30", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(factory.Eloverblik.TimeSeriesRequests);
    }

    [Fact]
    public async Task Production_meters_are_ignored_when_discovering_the_consumption_meter()
    {
        factory.Eloverblik.MeteringPoints = () => FakeEloverblik.MeteringPointsJson(
            ("571313100000000009", "E18"), (FakeEloverblik.MeteringPointId, "E17"));
        factory.Eloverblik.TimeSeries = () => FakeEloverblik.TimeSeriesJson(
            FakeEloverblik.HourlyPeriod(Utc(2025, 9, 29, 22), 1m));

        await _client.PostAsync("/api/ingest/consumption?from=2025-09-30&to=2025-09-30", null);

        Assert.Equal(FakeEloverblik.MeteringPointId, Assert.Single(await Stored()).MeteringPointId);
    }

    [Fact]
    public async Task Rate_limited_calls_wait_for_retry_after_and_then_succeed()
    {
        var calls = 0;
        factory.Eloverblik.TimeSeries = () =>
        {
            if (++calls > 1)
            {
                return FakeEloverblik.TimeSeriesJson(FakeEloverblik.HourlyPeriod(Utc(2025, 9, 29, 22), 1m));
            }

            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return limited;
        };

        var started = System.Diagnostics.Stopwatch.StartNew();
        var response = await _client.PostAsync("/api/ingest/consumption?from=2025-09-30&to=2025-09-30", null);
        started.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, factory.Eloverblik.TimeSeriesRequests.Count());
        // The test factory configures a 1 ms retry delay, so waiting about a second proves Retry-After was honoured.
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(900), $"Only waited {started.Elapsed}");
        Assert.Single(await Stored());
    }

    [Theory]
    [InlineData("from=2025-10-02&to=2025-10-01")]
    [InlineData("from=2023-01-01&to=2025-10-01")] // more than 730 days
    public async Task Invalid_ranges_are_rejected_without_calling_upstream(string query)
    {
        var response = await _client.PostAsync($"/api/ingest/consumption?{query}", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Eloverblik.Requests);
    }

    [Fact]
    public async Task Consumption_can_be_stored_before_and_independent_of_spot_prices()
    {
        // Different sources arrive at different times: consumption without any spot price must be accepted.
        factory.Eloverblik.TimeSeries = () => FakeEloverblik.TimeSeriesJson(
            FakeEloverblik.HourlyPeriod(Utc(2025, 9, 29, 22), 1m));
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.ExecuteAsync("TRUNCATE spot_price");

        var response = await _client.PostAsync("/api/ingest/consumption?from=2025-09-30&to=2025-09-30", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await Stored());
    }

    private async Task<List<StoredConsumption>> Stored()
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        var rows = await connection.QueryAsync<StoredConsumption>(
            """
            SELECT metering_point_id AS MeteringPointId, hour_start AS HourStart, kwh AS Kwh, quality AS Quality
            FROM consumption ORDER BY hour_start
            """);
        return rows.ToList();
    }
}
