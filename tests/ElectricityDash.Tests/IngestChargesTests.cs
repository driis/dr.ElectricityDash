using System.Net;
using System.Net.Http.Json;
using Dapper;
using ElectricityDash.Web.Eloverblik;
using Npgsql;

namespace ElectricityDash.Tests;

/// <summary>API-level tests for tariff, subscription and fee ingestion. Eloverblik faked at the HTTP level.</summary>
public class IngestChargesTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private record StoredCharge(
        string PriceId, DateTime ValidFrom, DateTime? ValidTo, string Kind, string Name, string PeriodType,
        decimal? Quantity, DateTime FirstSeenAt, DateTime LastSeenAt);

    private record StoredPrice(string PriceId, int Position, decimal Dkk);

    private static readonly DateTimeOffset FirstRun = new(2025, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        factory.Eloverblik.Reset();
        factory.Time.Now = FirstRun;
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.ExecuteAsync("TRUNCATE charge CASCADE");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static object FlatTariff(decimal price = 0.072m, string? validTo = null) =>
        FakeEloverblik.Tariff("TARIFF-FLAT", "Test system tariff", "P1D", "2017-03-31T22:00:00.000Z", validTo, price);

    private static object HourlyTariff() =>
        FakeEloverblik.Tariff("TARIFF-HOURLY", "Test grid tariff", "PT1H", "2020-09-30T22:00:00.000Z", null,
            [.. Enumerable.Repeat(0.06m, 6), .. Enumerable.Repeat(0.18m, 11), .. Enumerable.Repeat(0.55m, 4), .. Enumerable.Repeat(0.18m, 3)]);

    private static object GridSubscription(decimal price = 44.166667m) =>
        FakeEloverblik.Subscription("SUB-GRID", "Test grid subscription", "2017-03-31T22:00:00.000Z", price);

    [Fact]
    public async Task Tariffs_subscriptions_and_fees_are_stored_with_their_prices()
    {
        factory.Eloverblik.Charges = () => FakeEloverblik.ChargesJson(
            tariffs: [FlatTariff(), HourlyTariff()],
            subscriptions: [GridSubscription()],
            fees: [new
            {
                price = 10m, quantity = 2m, priceId = "FEE-1", name = "Test fee", description = "d", owner = "o",
                validFromDate = "2024-01-01T00:00:00.000Z", validToDate = (string?)null, periodType = "P1D"
            }]);

        var response = await _client.PostAsync("/api/ingest/charges", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChargesIngestResponse>();
        Assert.Equal(new ChargesIngestResponse(4, 4), body);

        var charges = (await Charges()).ToDictionary(c => c.PriceId);
        Assert.Equal(("tariff", "P1D", (decimal?)null), (charges["TARIFF-FLAT"].Kind, charges["TARIFF-FLAT"].PeriodType, charges["TARIFF-FLAT"].Quantity));
        Assert.Equal(("tariff", "PT1H"), (charges["TARIFF-HOURLY"].Kind, charges["TARIFF-HOURLY"].PeriodType));
        Assert.Equal(("subscription", "P1M", (decimal?)1m), (charges["SUB-GRID"].Kind, charges["SUB-GRID"].PeriodType, charges["SUB-GRID"].Quantity));
        Assert.Equal(("fee", (decimal?)2m), (charges["FEE-1"].Kind, charges["FEE-1"].Quantity));
        Assert.Equal(new DateTime(2017, 3, 31, 22, 0, 0, DateTimeKind.Utc), charges["TARIFF-FLAT"].ValidFrom);
        Assert.Null(charges["TARIFF-FLAT"].ValidTo);

        var prices = await Prices();
        Assert.Equal([new StoredPrice("TARIFF-FLAT", 1, 0.072m)], prices.Where(p => p.PriceId == "TARIFF-FLAT"));
        var hourly = prices.Where(p => p.PriceId == "TARIFF-HOURLY").ToList();
        Assert.Equal(Enumerable.Range(1, 24), hourly.Select(p => p.Position));
        Assert.Equal(0.06m, hourly[0].Dkk);
        Assert.Equal(0.55m, hourly[17].Dkk); // position 18 = 17:00-18:00 local, the peak band
        Assert.Equal([new StoredPrice("SUB-GRID", 1, 44.166667m)], prices.Where(p => p.PriceId == "SUB-GRID"));
    }

    [Fact]
    public async Task Ingesting_again_adds_no_versions_and_no_duplicate_prices()
    {
        factory.Eloverblik.Charges = () => FakeEloverblik.ChargesJson(tariffs: [HourlyTariff()], subscriptions: [GridSubscription()]);
        await _client.PostAsync("/api/ingest/charges", null);

        factory.Time.Now = FirstRun.AddDays(1);
        var response = await _client.PostAsync("/api/ingest/charges", null);

        Assert.Equal(new ChargesIngestResponse(2, 0), await response.Content.ReadFromJsonAsync<ChargesIngestResponse>());
        Assert.Equal(2, (await Charges()).Count);
        Assert.Equal(25, (await Prices()).Count); // 24 hourly + 1 subscription

        var tariff = (await Charges()).Single(c => c.PriceId == "TARIFF-HOURLY");
        Assert.Equal(FirstRun.UtcDateTime, tariff.FirstSeenAt);
        Assert.Equal(FirstRun.AddDays(1).UtcDateTime, tariff.LastSeenAt);
    }

    [Fact]
    public async Task A_new_version_is_added_and_the_old_one_is_kept_as_history()
    {
        factory.Eloverblik.Charges = () => FakeEloverblik.ChargesJson(tariffs: [FlatTariff(0.072m)]);
        await _client.PostAsync("/api/ingest/charges", null);

        // Later the grid company ends that version and a new one with a new price takes over.
        factory.Time.Now = FirstRun.AddDays(100);
        factory.Eloverblik.Charges = () => FakeEloverblik.ChargesJson(tariffs:
        [
            FlatTariff(0.072m, validTo: "2026-01-01T00:00:00.000Z"),
            FakeEloverblik.Tariff("TARIFF-FLAT", "Test system tariff", "P1D", "2026-01-01T00:00:00.000Z", null, 0.09m)
        ]);
        var response = await _client.PostAsync("/api/ingest/charges", null);

        Assert.Equal(new ChargesIngestResponse(2, 1), await response.Content.ReadFromJsonAsync<ChargesIngestResponse>());
        var charges = (await Charges()).OrderBy(c => c.ValidFrom).ToList();
        Assert.Equal(2, charges.Count);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), charges[0].ValidTo);
        Assert.Null(charges[1].ValidTo);
        Assert.Equal(FirstRun.AddDays(100).UtcDateTime, charges[1].FirstSeenAt);
        Assert.Equal(FirstRun.UtcDateTime, charges[0].FirstSeenAt);
        Assert.Equal([0.072m, 0.09m], (await Prices()).OrderBy(p => p.Dkk).Select(p => p.Dkk));
    }

    [Fact]
    public async Task A_version_that_is_no_longer_returned_is_kept()
    {
        factory.Eloverblik.Charges = () => FakeEloverblik.ChargesJson(tariffs: [FlatTariff()], subscriptions: [GridSubscription()]);
        await _client.PostAsync("/api/ingest/charges", null);

        factory.Time.Now = FirstRun.AddDays(1);
        factory.Eloverblik.Charges = () => FakeEloverblik.ChargesJson(subscriptions: [GridSubscription()]);
        await _client.PostAsync("/api/ingest/charges", null);

        var charges = await Charges();
        Assert.Equal(2, charges.Count);
        // Its last_seen_at shows it was not returned by the latest ingest.
        Assert.Equal(FirstRun.UtcDateTime, charges.Single(c => c.PriceId == "TARIFF-FLAT").LastSeenAt);
    }

    [Fact]
    public async Task A_corrected_price_replaces_the_old_price_of_the_same_version()
    {
        factory.Eloverblik.Charges = () => FakeEloverblik.ChargesJson(tariffs: [HourlyTariff()]);
        await _client.PostAsync("/api/ingest/charges", null);

        factory.Eloverblik.Charges = () => FakeEloverblik.ChargesJson(tariffs:
        [
            FakeEloverblik.Tariff("TARIFF-FLAT", "Test system tariff", "P1D", "2017-03-31T22:00:00.000Z", null, 0.08m),
            FakeEloverblik.Tariff("TARIFF-HOURLY", "Test grid tariff", "PT1H", "2020-09-30T22:00:00.000Z", null,
                [.. Enumerable.Repeat(0.10m, 24)])
        ]);
        await _client.PostAsync("/api/ingest/charges", null);

        var hourly = (await Prices()).Where(p => p.PriceId == "TARIFF-HOURLY").ToList();
        Assert.Equal(24, hourly.Count);
        Assert.All(hourly, p => Assert.Equal(0.10m, p.Dkk));
    }

    [Fact]
    public async Task Upstream_failure_gives_bad_gateway_and_stores_nothing()
    {
        factory.Eloverblik.Charges = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var response = await _client.PostAsync("/api/ingest/charges", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await Charges());
    }

    [Fact]
    public async Task A_charge_without_an_id_fails_the_whole_ingest_rather_than_storing_part_of_it()
    {
        factory.Eloverblik.Charges = () => FakeEloverblik.ChargesJson(tariffs:
        [
            FlatTariff(),
            FakeEloverblik.Tariff(null!, "Broken", "P1D", "2017-03-31T22:00:00.000Z", null, 0.1m)
        ]);

        var response = await _client.PostAsync("/api/ingest/charges", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await Charges());
    }

    [Fact]
    public async Task No_charges_is_not_an_error()
    {
        var response = await _client.PostAsync("/api/ingest/charges", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new ChargesIngestResponse(0, 0), await response.Content.ReadFromJsonAsync<ChargesIngestResponse>());
    }

    private async Task<List<StoredCharge>> Charges()
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        var rows = await connection.QueryAsync<StoredCharge>(
            """
            SELECT price_id AS PriceId, valid_from AS ValidFrom, valid_to AS ValidTo, kind AS Kind, name AS Name,
                   period_type AS PeriodType, quantity AS Quantity, first_seen_at AS FirstSeenAt, last_seen_at AS LastSeenAt
            FROM charge ORDER BY price_id, valid_from
            """);
        return rows.ToList();
    }

    private async Task<List<StoredPrice>> Prices()
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        var rows = await connection.QueryAsync<StoredPrice>(
            "SELECT price_id AS PriceId, position AS Position, dkk AS Dkk FROM charge_price ORDER BY price_id, valid_from, position");
        return rows.ToList();
    }
}
