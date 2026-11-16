using Dapper;
using Npgsql;

namespace ElectricityDash.Web.Eloverblik;

public sealed record ChargeUpsertResult(int Seen, int NewVersions);

public sealed class ChargeRepository(NpgsqlDataSource db)
{
    // xmax = 0 only for a freshly inserted row, which tells new versions from updates of known ones.
    private const string UpsertChargeSql = """
        INSERT INTO charge (price_id, valid_from, valid_to, kind, name, description, owner, period_type, quantity, first_seen_at, last_seen_at)
        VALUES (@PriceId, @ValidFrom, @ValidTo, @Kind, @Name, @Description, @Owner, @PeriodType, @Quantity, @SeenAt, @SeenAt)
        ON CONFLICT (price_id, valid_from) DO UPDATE SET
            valid_to = EXCLUDED.valid_to, kind = EXCLUDED.kind, name = EXCLUDED.name, description = EXCLUDED.description,
            owner = EXCLUDED.owner, period_type = EXCLUDED.period_type, quantity = EXCLUDED.quantity,
            last_seen_at = EXCLUDED.last_seen_at
        RETURNING (xmax = 0)
        """;

    private const string DeletePricesSql = "DELETE FROM charge_price WHERE price_id = @PriceId AND valid_from = @ValidFrom";

    private const string InsertPriceSql = """
        INSERT INTO charge_price (price_id, valid_from, position, dkk)
        VALUES (@PriceId, @ValidFrom, @Position, @Dkk)
        """;

    /// <summary>
    /// Records the charge versions Eloverblik currently reports. A known version is updated in place (valid_to set, prices corrected),
    /// an unknown one is added, and versions Eloverblik no longer returns are kept, which is how history accumulates.
    /// </summary>
    public async Task<ChargeUpsertResult> UpsertAsync(IReadOnlyList<Charge> charges, DateTimeOffset seenAt, CancellationToken ct)
    {
        if (charges.Count == 0) return new ChargeUpsertResult(0, 0);

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var newVersions = 0;
        foreach (var charge in charges)
        {
            var inserted = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(UpsertChargeSql, new
            {
                charge.PriceId,
                charge.ValidFrom,
                charge.ValidTo,
                charge.Kind,
                charge.Name,
                charge.Description,
                charge.Owner,
                charge.PeriodType,
                charge.Quantity,
                SeenAt = seenAt
            }, tx, cancellationToken: ct));
            if (inserted) newVersions++;

            var key = new { charge.PriceId, charge.ValidFrom };
            await connection.ExecuteAsync(new CommandDefinition(DeletePricesSql, key, tx, cancellationToken: ct));
            var prices = charge.Prices.Select(p => new { charge.PriceId, charge.ValidFrom, p.Position, p.Dkk });
            await connection.ExecuteAsync(new CommandDefinition(InsertPriceSql, prices, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return new ChargeUpsertResult(charges.Count, newVersions);
    }
}
