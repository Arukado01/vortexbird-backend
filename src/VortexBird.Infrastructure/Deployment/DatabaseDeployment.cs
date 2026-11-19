using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace VortexBird.Infrastructure.Deployment;

public static class DatabaseDeployment
{
    public static async Task Run(DeploymentOptions options, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(ct);
        await using var db = new IntegrationDbContext(new DbContextOptionsBuilder<IntegrationDbContext>().UseNpgsql(connection).Options);
        RuntimeDatabasePermissions.ValidateModel(db);
        await RuntimeDatabasePermissions.ValidateRoles(connection, options, ct);
        await using var acquire = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended('vortexbird:deployment', 0))", connection);
        if (await acquire.ExecuteScalarAsync(ct) is not true)
            throw new DeploymentPolicyException("Another VortexBird deployment job holds the migration lock.");
        try
        {
            if (options.Migrate) await db.Database.MigrateAsync(ct);
            if ((await db.Database.GetPendingMigrationsAsync(ct)).Any())
                throw new DeploymentPolicyException("Pending migrations must be applied before administrator bootstrap.");
            await RuntimeDatabasePermissions.Apply(connection, options.RuntimeRoles, ct);
            if (options.Administrator is { } administrator) await InitialAdminBootstrap.Run(db, administrator, ct);
        }
        finally
        {
            // Closing the session also releases the lock if cancellation or a connection failure prevents this statement.
            if (connection.State == System.Data.ConnectionState.Open)
            {
                await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended('vortexbird:deployment', 0))", connection);
                await release.ExecuteScalarAsync(CancellationToken.None);
            }
        }
    }
}
