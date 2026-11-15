using Dapper;
using Npgsql;

namespace ElectricityDash.Web.SpotPrices;

public sealed class SpotPriceRepository(NpgsqlDataSource db)
{
    private const string UpsertSql = """
        INSERT INTO spot_price (price_area, hour_start, dkk_per_mwh)
        VALUES (@PriceArea, @HourStart, @DkkPerMwh)
        ON CONFLICT (price_area, hour_start) DO UPDATE SET dkk_per_mwh = EXCLUDED.dkk_per_mwh
        """;

    /// <summary>Inserts or updates prices by (price area, hour). Safe to call repeatedly with overlapping data.</summary>
    public async Task UpsertAsync(string priceArea, IReadOnlyList<HourlySpotPrice> prices, CancellationToken ct)
    {
        if (prices.Count == 0) return;

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var rows = prices.Select(p => new { PriceArea = priceArea, HourStart = p.HourStartUtc, p.DkkPerMwh });
        await connection.ExecuteAsync(new CommandDefinition(UpsertSql, rows, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }
}
