using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ElectricityDash.Tests;

/// <summary>A request the fake saw. <see cref="Authorization"/> is the bearer token, <see cref="Body"/> the JSON body (if any).</summary>
public sealed record EloverblikRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Body);

/// <summary>
/// Stands in for api.eloverblik.dk at the HTTP level, so the real client, resilience pipeline and JSON parsing are exercised.
/// Never reaches the network. All data is synthetic. Response shapes follow what the real API returns.
/// </summary>
public sealed class FakeEloverblik : HttpMessageHandler
{
    public const string RefreshToken = "test-refresh-token";
    public const string AccessToken = "test-access-token";
    public const string MeteringPointId = "571313100000000000";

    private readonly List<EloverblikRequest> _requests = [];

    public IReadOnlyList<EloverblikRequest> Requests => _requests;

    public IEnumerable<EloverblikRequest> TimeSeriesRequests => _requests.Where(r => r.Uri.AbsolutePath.Contains("/gettimeseries/"));

    public Func<HttpResponseMessage> MeteringPoints { get; set; } = DefaultMeteringPoints;

    public Func<HttpResponseMessage> TimeSeries { get; set; } = () => TimeSeriesJson();

    public Func<HttpResponseMessage> Charges { get; set; } = () => ChargesJson();

    public void Reset()
    {
        _requests.Clear();
        MeteringPoints = DefaultMeteringPoints;
        TimeSeries = () => TimeSeriesJson();
        Charges = () => ChargesJson();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var auth = request.Headers.Authorization?.Parameter;
        _requests.Add(new EloverblikRequest(request.Method, request.RequestUri!, auth, body));

        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/token"))
        {
            return auth == RefreshToken
                ? Json(new { result = AccessToken })
                : new HttpResponseMessage(HttpStatusCode.Forbidden); // like the real API for a non-refresh token
        }

        // Every other endpoint needs the short-lived data access token.
        if (auth != AccessToken) return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        if (path.EndsWith("/meteringpoints/meteringpoints")) return MeteringPoints();
        if (path.Contains("/gettimeseries/")) return TimeSeries();
        if (path.EndsWith("/getcharges")) return Charges();
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage DefaultMeteringPoints() =>
        MeteringPointsJson((MeteringPointId, "E17"));

    public static HttpResponseMessage MeteringPointsJson(params (string Id, string Type)[] points) =>
        Json(new { result = points.Select(p => new { meteringPointId = p.Id, typeOfMP = p.Type }) });

    /// <summary>One Danish day of hourly data. <paramref name="kwh"/> holds one value per hour from <paramref name="startUtc"/>, null leaves a gap.</summary>
    public static object Period(DateTime startUtc, string resolution, string quality, params decimal?[] kwh) => new Dictionary<string, object>
    {
        ["resolution"] = resolution,
        ["timeInterval"] = new
        {
            start = Utc(startUtc),
            end = Utc(startUtc.AddHours(kwh.Length))
        },
        ["Point"] = kwh
            .Select((value, i) => (value, position: i + 1))
            .Where(p => p.value is not null)
            .Select(p => new Dictionary<string, object>
            {
                ["position"] = p.position.ToString(CultureInfo.InvariantCulture),
                ["out_Quantity.quantity"] = p.value!.Value.ToString("0.000", CultureInfo.InvariantCulture),
                ["out_Quantity.quality"] = quality
            })
            .ToList()
    };

    public static object HourlyPeriod(DateTime startUtc, params decimal?[] kwh) => Period(startUtc, "PT1H", "A04", kwh);

    public static HttpResponseMessage TimeSeriesJson(params object[] periods) => TimeSeriesJson("KWH", periods);

    public static HttpResponseMessage TimeSeriesJson(string unit, params object[] periods) => Json(new
    {
        result = new[]
        {
            new Dictionary<string, object>
            {
                ["MyEnergyData_MarketDocument"] = new
                {
                    TimeSeries = periods.Length == 0
                        ? []
                        : new[]
                        {
                            new Dictionary<string, object>
                            {
                                ["businessType"] = "A04",
                                ["measurement_Unit.name"] = unit,
                                ["Period"] = periods
                            }
                        }
                },
                ["success"] = true,
                ["errorCode"] = 10000,
                ["errorText"] = ""
            }
        }
    });

    public static HttpResponseMessage ErrorJson(int errorCode, string errorText) => Json(new
    {
        result = new[] { new { success = false, errorCode, errorText } }
    });

    public static object Tariff(string priceId, string name, string periodType, string validFrom, string? validTo, params decimal[] prices) => new
    {
        prices = prices.Select((p, i) => new { position = (i + 1).ToString(CultureInfo.InvariantCulture), price = p }),
        priceId, name, description = $"{name} description", owner = "Test Grid Company",
        validFromDate = validFrom, validToDate = validTo, periodType
    };

    public static object Subscription(string priceId, string name, string validFrom, decimal price, decimal quantity = 1) => new
    {
        price, quantity, priceId, name, description = $"{name} description", owner = "Test Grid Company",
        validFromDate = validFrom, validToDate = (string?)null, periodType = "P1M"
    };

    public static HttpResponseMessage ChargesJson(
        IEnumerable<object>? tariffs = null, IEnumerable<object>? subscriptions = null, IEnumerable<object>? fees = null) => Json(new
    {
        result = new[]
        {
            new
            {
                result = new
                {
                    fees = fees ?? [],
                    meteringPointId = MeteringPointId,
                    subscriptions = subscriptions ?? [],
                    tariffs = tariffs ?? []
                },
                success = true,
                errorCode = 10000,
                errorText = ""
            }
        }
    });

    private static string Utc(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };
}
