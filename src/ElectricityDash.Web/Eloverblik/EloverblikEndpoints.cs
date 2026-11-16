using ElectricityDash.Web.SpotPrices;

namespace ElectricityDash.Web.Eloverblik;

public static class EloverblikEndpoints
{
    // Eloverblik allows at most 730 days per time series call.
    private const int MaxDays = 730;

    // Eloverblik data arrives a day or more late and recent values can be revised, so by default re-read the last week.
    private const int DefaultLookbackDays = 7;

    public static void MapEloverblikIngestEndpoints(this IEndpointRouteBuilder app)
    {
        // Intended to be called by a scheduled job. Idempotent: safe to call repeatedly and with overlapping ranges.
        // No authentication yet (local use only), see "Open decisions" in CLAUDE.md before exposing publicly.
        app.MapPost("/api/ingest/consumption", async (
            DateOnly? from, DateOnly? to,
            ConsumptionIngestor ingestor, TimeProvider time, CancellationToken ct) =>
        {
            var today = SpotPriceIngestor.DanishDate(time.GetUtcNow());
            var fromDate = from ?? today.AddDays(-DefaultLookbackDays);
            var toDate = to ?? today;

            if (toDate < fromDate)
                return Results.Problem("'to' must not be before 'from'.", statusCode: StatusCodes.Status400BadRequest);
            if (toDate.DayNumber - fromDate.DayNumber + 1 > MaxDays)
                return Results.Problem($"At most {MaxDays} days can be ingested per call.", statusCode: StatusCodes.Status400BadRequest);

            try
            {
                var hours = await ingestor.IngestAsync(fromDate, toDate, ct);
                return Results.Ok(new ConsumptionIngestResponse(fromDate, toDate, hours));
            }
            catch (EloverblikException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Charges are not time ranged: Eloverblik returns the versions valid now, and we record what is new.
        app.MapPost("/api/ingest/charges", async (ChargeIngestor ingestor, CancellationToken ct) =>
        {
            try
            {
                var result = await ingestor.IngestAsync(ct);
                return Results.Ok(new ChargesIngestResponse(result.Seen, result.NewVersions));
            }
            catch (EloverblikException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }
}

public sealed record ConsumptionIngestResponse(DateOnly From, DateOnly To, int HoursStored);

public sealed record ChargesIngestResponse(int ChargesSeen, int NewVersions);
