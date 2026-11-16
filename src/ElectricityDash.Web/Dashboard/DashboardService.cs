using Dapper;
using ElectricityDash.Web.SpotPrices;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ElectricityDash.Web.Dashboard;

public sealed class DashboardService(NpgsqlDataSource db, TimeProvider time, IOptions<EnergyDataServiceOptions> spotOptions)
{
    private const string PricesSql = """
        SELECT hour_start AS HourStartUtc, dkk_per_mwh / 1000 AS DkkPerKwh
        FROM spot_price
        WHERE price_area = @PriceArea AND hour_start >= @FromUtc AND hour_start < @ToUtc
        ORDER BY hour_start
        """;

    // One meter, but summing per hour keeps this correct if Eloverblik ever returns more than one.
    private const string UsageSql = """
        SELECT hour_start AS HourStartUtc, sum(kwh) AS Kwh
        FROM consumption
        WHERE hour_start >= @FromUtc AND hour_start < @ToUtc
        GROUP BY hour_start
        ORDER BY hour_start
        """;

    public DateOnly Today => DashboardCalculator.LocalDate(time.GetUtcNow().UtcDateTime);

    public async Task<DashboardData> GetAsync(string? periodKey, CancellationToken ct = default)
    {
        var period = Periods.Resolve(periodKey, Today);
        var args = new
        {
            PriceArea = spotOptions.Value.PriceArea,
            FromUtc = DashboardCalculator.StartOfDayUtc(period.From),
            ToUtc = DashboardCalculator.StartOfDayUtc(period.To.AddDays(1)),
        };

        await using var connection = await db.OpenConnectionAsync(ct);
        var prices = (await connection.QueryAsync<HourlyPrice>(new CommandDefinition(PricesSql, args, cancellationToken: ct))).AsList();
        var usage = (await connection.QueryAsync<HourlyUsage>(new CommandDefinition(UsageSql, args, cancellationToken: ct))).AsList();

        return DashboardCalculator.Calculate(period, prices, usage);
    }
}
