using System.Net;
using System.Net.Http.Json;
using Dapper;
using ElectricityDash.Web.Database;
using Npgsql;

namespace ElectricityDash.Tests;

public class HealthEndpointTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public HealthEndpointTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_reports_ok_and_applied_migrations()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("ok", body?.Status);
        Assert.True(body?.AppliedMigrations >= 1);
    }

    [Fact]
    public async Task Migrations_create_schema_and_are_idempotent()
    {
        _factory.CreateClient(); // starts the host, which runs the migrations

        var rerun = DatabaseMigrator.Migrate(_factory.ConnectionString);
        Assert.True(rerun.Successful);

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        var tables = (await connection.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'")).ToList();
        Assert.Contains("spot_price", tables);
        Assert.Contains("consumption", tables);
    }
}
