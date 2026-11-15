using Dapper;
using ElectricityDash.Web.Components;
using ElectricityDash.Web.Database;
using ElectricityDash.Web.SpotPrices;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("ElectricityDash")
    ?? throw new InvalidOperationException("Connection string 'ElectricityDash' is not configured.");

var energyDataService = builder.Configuration.GetSection("EnergyDataService").Get<EnergyDataServiceOptions>()
    ?? new EnergyDataServiceOptions();

builder.Services.AddNpgsqlDataSource(connectionString);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<EnergyDataServiceOptions>(builder.Configuration.GetSection("EnergyDataService"));
builder.Services.AddHttpClient<EnergyDataServiceClient>(client => client.BaseAddress = energyDataService.Endpoint)
    .AddStandardResilienceHandler(o =>
    {
        o.Retry.Delay = energyDataService.RetryDelay;
        o.Retry.MaxRetryAttempts = energyDataService.MaxRetries;
        o.Retry.ShouldRetryAfterHeader = true; // Energi Data Service rate limits with 429 + Retry-After
        o.TotalRequestTimeout.Timeout = energyDataService.TotalTimeout;
    });
builder.Services.AddScoped<SpotPriceRepository>();
builder.Services.AddScoped<SpotPriceIngestor>();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var migration = DatabaseMigrator.Migrate(connectionString);
if (!migration.Successful)
{
    throw new InvalidOperationException("Database migration failed.", migration.Error);
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

app.MapGet("/api/health", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var connection = await db.OpenConnectionAsync(ct);
    var appliedMigrations = await connection.ExecuteScalarAsync<int>(
        new CommandDefinition("SELECT count(*) FROM schemaversions", cancellationToken: ct));
    return new HealthResponse("ok", appliedMigrations);
});

app.MapIngestEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public record HealthResponse(string Status, int AppliedMigrations);

// Makes the entry point visible to WebApplicationFactory in the test project.
public partial class Program;
