using DbUp;
using ElectricityDash.Web.SpotPrices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ElectricityDash.Tests;

/// <summary>
/// Hosts the real app against a real Postgres. Each factory instance gets its own throwaway database,
/// created by the app's DbUp migrations on startup and dropped on dispose.
/// Server comes from TEST_POSTGRES_CONNECTION (defaults to the docker-compose Postgres).
/// External HTTP APIs are replaced by fakes, see <see cref="EnergyDataService"/>.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private const string DefaultAdminConnection =
        "Host=localhost;Port=5432;Database=postgres;Username=electricitydash;Password=electricitydash";

    public string ConnectionString { get; }

    public FakeEnergyDataService EnergyDataService { get; } = new();

    public FixedTimeProvider Time { get; } = new(new DateTimeOffset(2025, 10, 1, 10, 0, 0, TimeSpan.Zero));

    public ApiFactory()
    {
        var admin = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION") ?? DefaultAdminConnection;
        ConnectionString = new NpgsqlConnectionStringBuilder(admin)
        {
            Database = $"electricitydash_test_{Guid.NewGuid():N}"
        }.ConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:ElectricityDash", ConnectionString);
        builder.UseSetting("EnergyDataService:RetryDelay", "00:00:00.001"); // keep failure scenarios fast

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<EnergyDataServiceClient>()
                .ConfigurePrimaryHttpMessageHandler(() => EnergyDataService);
            services.AddSingleton<TimeProvider>(Time);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        NpgsqlConnection.ClearAllPools();
        DropDatabase.For.PostgresqlDatabase(ConnectionString);
    }
}
