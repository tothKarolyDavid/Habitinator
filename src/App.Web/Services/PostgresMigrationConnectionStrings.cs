using Npgsql;

namespace App.Web.Services;

/// <summary>
/// Neon and similar pooled endpoints use PgBouncer transaction pooling, which is not reliable for
/// EF Core <see cref="Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync" />.
/// Derives a direct compute hostname from the standard Neon pooled host pattern when needed.
/// </summary>
public static class PostgresMigrationConnectionStrings
{
    public static string ResolvePrimary(IConfiguration configuration) =>
        configuration.GetConnectionString("habitinatordb")
        ?? configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "No PostgreSQL connection string configured. Set ConnectionStrings:DefaultConnection or run through Aspire (habitinatordb).");

    /// <summary>
    /// Connection string used only for applying EF migrations at startup. Prefer direct Neon host
    /// over <c>-pooler</c>. Override with <c>ConnectionStrings:MigrationConnection</c> if needed.
    /// </summary>
    public static string ResolveForMigrations(IConfiguration configuration)
    {
        var explicitMigration = configuration.GetConnectionString("MigrationConnection");
        if (!string.IsNullOrWhiteSpace(explicitMigration))
        {
            return explicitMigration;
        }

        var primary = ResolvePrimary(configuration);
        return TryNeonDirectFromPooler(primary) ?? primary;
    }

    internal static string? TryNeonDirectFromPooler(string connectionString)
    {
        try
        {
            var b = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrEmpty(b.Host))
            {
                return null;
            }

            if (!b.Host.Contains("neon.tech", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            // Neon pooled hosts: ep-...-pooler.<region>.aws.neon.tech
            if (!b.Host.Contains("-pooler.", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            b.Host = b.Host.Replace("-pooler.", ".", StringComparison.OrdinalIgnoreCase);
            return b.ConnectionString;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
