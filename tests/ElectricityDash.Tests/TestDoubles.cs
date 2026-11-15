using System.Net;
using System.Text;
using System.Text.Json;

namespace ElectricityDash.Tests;

/// <summary>
/// Stands in for api.energidataservice.dk at the HTTP level, so the real client, resilience pipeline and
/// JSON parsing are exercised. Never reaches the network.
/// </summary>
public sealed class FakeEnergyDataService : HttpMessageHandler
{
    private readonly List<Uri> _requests = [];

    public IReadOnlyList<Uri> Requests => _requests;

    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ => Json([]);

    public void Reset()
    {
        _requests.Clear();
        Responder = _ => Json([]);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Add(request.RequestUri!);
        return Task.FromResult(Responder(request));
    }

    public static HttpResponseMessage Json(IEnumerable<object> records) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { total = records.Count(), dataset = "DayAheadPrices", records }),
            Encoding.UTF8, "application/json")
    };

    /// <summary>One record per 15 minutes starting at <paramref name="startUtc"/>, in the format the real API uses (UTC without offset).</summary>
    public static IEnumerable<object> Quarters(DateTime startUtc, string priceArea, params decimal?[] dkkPerMwh) =>
        dkkPerMwh.Select((dkk, i) => new
        {
            TimeUTC = startUtc.AddMinutes(15 * i).ToString("yyyy-MM-dd'T'HH:mm:ss"),
            TimeDK = startUtc.AddMinutes(15 * i).AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss"),
            PriceArea = priceArea,
            DayAheadPriceEUR = (dkk ?? 0) / 7.46m,
            DayAheadPriceDKK = dkk
        });
}

public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
