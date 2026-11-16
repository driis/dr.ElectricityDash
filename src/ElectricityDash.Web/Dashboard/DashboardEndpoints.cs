namespace ElectricityDash.Web.Dashboard;

public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/dashboard", async (string? period, DashboardService dashboard, CancellationToken ct) =>
            await dashboard.GetAsync(period, ct));
}
