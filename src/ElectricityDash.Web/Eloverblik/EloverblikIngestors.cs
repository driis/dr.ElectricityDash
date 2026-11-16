namespace ElectricityDash.Web.Eloverblik;

public sealed class ConsumptionIngestor(EloverblikClient client, ConsumptionRepository repository, TimeProvider time)
{
    /// <summary>Fetches hourly consumption for the Danish calendar days <paramref name="from"/> to <paramref name="to"/> (inclusive) and upserts it.</summary>
    public async Task<int> IngestAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var meteringPointId = await client.GetMeteringPointIdAsync(ct);
        // Eloverblik's dateTo is exclusive, ours is inclusive.
        var hours = await client.GetHourlyConsumptionAsync(meteringPointId, from, to.AddDays(1), ct);
        await repository.UpsertAsync(meteringPointId, hours, time.GetUtcNow(), ct);
        return hours.Count;
    }
}

public sealed class ChargeIngestor(EloverblikClient client, ChargeRepository repository, TimeProvider time)
{
    public async Task<ChargeUpsertResult> IngestAsync(CancellationToken ct)
    {
        var meteringPointId = await client.GetMeteringPointIdAsync(ct);
        var charges = await client.GetChargesAsync(meteringPointId, ct);
        return await repository.UpsertAsync(charges, time.GetUtcNow(), ct);
    }
}
