namespace ElectricityDash.Web.SpotPrices;

public static class IngestEndpoints
{
    private const int MaxDays = 366;

    public static void MapIngestEndpoints(this IEndpointRouteBuilder app)
    {
        // Intended to be called by a scheduled job. Idempotent: safe to call repeatedly and with overlapping ranges.
        // No authentication yet (local use only), see "Open decisions" in CLAUDE.md before exposing publicly.
        app.MapPost("/api/ingest/spot-prices", async (
            DateOnly? from, DateOnly? to,
            SpotPriceIngestor ingestor, TimeProvider time, CancellationToken ct) =>
        {
            // Defaults to today and tomorrow (Danish dates). Tomorrow's prices are published around 13:00 CET.
            var today = SpotPriceIngestor.DanishDate(time.GetUtcNow());
            var fromDate = from ?? today;
            var toDate = to ?? today.AddDays(1);

            if (toDate < fromDate)
                return Results.Problem("'to' must not be before 'from'.", statusCode: StatusCodes.Status400BadRequest);
            if (toDate.DayNumber - fromDate.DayNumber + 1 > MaxDays)
                return Results.Problem($"At most {MaxDays} days can be ingested per call.", statusCode: StatusCodes.Status400BadRequest);

            try
            {
                var result = await ingestor.IngestAsync(fromDate, toDate, ct);
                return Results.Ok(new IngestResponse(fromDate, toDate, result.HoursStored));
            }
            catch (EnergyDataServiceException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }
}

public sealed record IngestResponse(DateOnly From, DateOnly To, int HoursStored);
