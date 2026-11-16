using Dapper;
using Npgsql;

namespace ElectricityDash.Web.Eloverblik;

public sealed class ConsumptionRepository(NpgsqlDataSource db)
{
    private const string UpsertSql = """
        INSERT INTO consumption (metering_point_id, hour_start, kwh, quality, imported_at)
        VALUES (@MeteringPointId, @HourStart, @Kwh, @Quality, @ImportedAt)
        ON CONFLICT (metering_point_id, hour_start)
        DO UPDATE SET kwh = EXCLUDED.kwh, quality = EXCLUDED.quality, imported_at = EXCLUDED.imported_at
        """;

    /// <summary>Inserts or updates consumption by (meter, hour). Eloverblik revises recent values, so overlapping ranges are expected.</summary>
    public async Task UpsertAsync(
        string meteringPointId, IReadOnlyList<HourlyConsumption> hours, DateTimeOffset importedAt, CancellationToken ct)
    {
        if (hours.Count == 0) return;

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var rows = hours.Select(h => new
        {
            MeteringPointId = meteringPointId,
            HourStart = h.HourStartUtc,
            h.Kwh,
            h.Quality,
            ImportedAt = importedAt
        });
        await connection.ExecuteAsync(new CommandDefinition(UpsertSql, rows, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }
}
