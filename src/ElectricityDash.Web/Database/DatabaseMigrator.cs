using System.Reflection;
using DbUp;
using DbUp.Engine;

namespace ElectricityDash.Web.Database;

/// <summary>Creates the database if missing and applies embedded SQL scripts in order.</summary>
public static class DatabaseMigrator
{
    public static DatabaseUpgradeResult Migrate(string connectionString)
    {
        EnsureDatabase.For.PostgresqlDatabase(connectionString);

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly(), name => name.EndsWith(".sql"))
            .LogToConsole()
            .Build();

        return upgrader.PerformUpgrade();
    }
}
