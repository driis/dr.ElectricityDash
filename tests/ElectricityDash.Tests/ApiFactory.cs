using DbUp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace ElectricityDash.Tests;

/// <summary>
/// Hosts the real app against a real Postgres. Each factory instance gets its own throwaway database,
/// created by the app's DbUp migrations on startup and dropped on dispose.
/// Server comes from TEST_POSTGRES_CONNECTION (defaults to the docker-compose Postgres).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private const string DefaultAdminConnection =
        "Host=localhost;Port=5432;Database=postgres;Username=electricitydash;Password=electricitydash";

    public string ConnectionString { get; }

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
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        NpgsqlConnection.ClearAllPools();
        DropDatabase.For.PostgresqlDatabase(ConnectionString);
    }
}
