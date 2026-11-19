using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace VortexBird.Infrastructure.Deployment;

public static class RuntimeDatabasePermissions
{
    public static readonly IReadOnlyDictionary<string, string> Tables = BuildPolicy();
    private static Dictionary<string, string> BuildPolicy()
    {
        var rules = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in new[] { "Partners", "Channels", "Products", "Users", "Orders", "InventoryReservations", "Payments", "OutboxMessages", "OmsReplicas", "WebhookSubscriptions", "WebhookDeliveries", "PaymentCheckouts", "PaymentInbox", "PaymentReceipts", "MessagingInbox", "QuotaCounters" })
            rules.Add(table, "SELECT, INSERT, UPDATE");
        foreach (var table in new[] { "OrderItems", "OrderHistories", "IdempotencyRecords", "ProcessedMessages", "ActivityLogs", "IntegrationExecutions", "Permissions" })
            rules.Add(table, "SELECT, INSERT");
        foreach (var table in new[] { "Stocks", "Prices", "Integrations" }) rules.Add(table, "SELECT, UPDATE");
        rules.Add("Roles", "SELECT, INSERT, UPDATE, DELETE");
        rules.Add("UserRoles", "SELECT, INSERT, DELETE");
        rules.Add("RolePermissions", "SELECT, INSERT, DELETE");
        rules.Add("Customers", "SELECT");
        rules.Add("__EFMigrationsHistory", "SELECT");
        return rules;
    }

    public static void ValidateModel(IntegrationDbContext db)
    {
        var modelTables = db.Model.GetEntityTypes().Select(entity => entity.GetTableName()).OfType<string>().Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (!modelTables.SetEquals(Tables.Keys.Where(table => table != "__EFMigrationsHistory"))
            || db.Model.GetEntityTypes().Any(entity => entity.GetSchema() is { } schema && schema != "public")
            || db.Model.GetSequences().Any())
            throw new DeploymentPolicyException("The application model changed; review the explicit runtime database permission policy before deployment.");
    }

    public static string GrantStatement(string table, string role)
    {
        if (!Tables.TryGetValue(table, out var permissions) || !DeploymentOptions.ValidRoleName(role))
            throw new DeploymentConfigurationException("Only application tables and validated runtime role names may be granted.");
        var relation = "public." + Quote(table);
        return $"REVOKE ALL PRIVILEGES ON TABLE {relation} FROM {Quote(role)}; GRANT {permissions} ON TABLE {relation} TO {Quote(role)};";
    }

    public static async Task ValidateRoles(NpgsqlConnection connection, DeploymentOptions options, CancellationToken ct)
    {
        await using (var migrator = new NpgsqlCommand("""
            SELECT current_user = @role AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole
              AND NOT rolreplication AND NOT rolbypassrls
              AND has_schema_privilege(current_user, 'public', 'CREATE')
              AND (SELECT pg_get_userbyid(datdba) = current_user FROM pg_database WHERE datname = current_database())
            FROM pg_roles WHERE rolname = current_user
            """, connection))
        {
            migrator.Parameters.AddWithValue("role", options.MigratorRole);
            if (await migrator.ExecuteScalarAsync(ct) is not true)
                throw new DeploymentPolicyException("The migration connection must use the dedicated database owner without cluster-administration privileges.");
        }
        foreach (var role in options.RuntimeRoles)
        {
            await using var command = new NpgsqlCommand("""
                SELECT rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole
                  AND NOT rolreplication AND NOT rolbypassrls
                  AND NOT EXISTS (SELECT 1 FROM pg_auth_members WHERE member = r.oid)
                  AND NOT EXISTS (SELECT 1 FROM pg_database WHERE datdba = r.oid)
                  AND NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspowner = r.oid)
                  AND NOT EXISTS (SELECT 1 FROM pg_class WHERE relowner = r.oid)
                  AND NOT has_schema_privilege(r.oid, 'public', 'CREATE')
                  AND NOT has_database_privilege(r.oid, current_database(), 'CREATE,TEMPORARY')
                FROM pg_roles r WHERE rolname = @role
                """, connection);
            command.Parameters.AddWithValue("role", role);
            if (await command.ExecuteScalarAsync(ct) is not true)
                throw new DeploymentPolicyException("A runtime role is missing or has ownership, role memberships, administrative or DDL privileges; provision dedicated roles first.");
        }
    }

    public static async Task Apply(NpgsqlConnection connection, string[] roles, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var role in roles)
        {
            await using (var access = new NpgsqlCommand($"GRANT CONNECT ON DATABASE {Quote(connection.Database)} TO {Quote(role)}; GRANT USAGE ON SCHEMA public TO {Quote(role)};", connection, transaction))
                await access.ExecuteNonQueryAsync(ct);
            foreach (var (table, allowed) in Tables)
            {
                await using (var grant = new NpgsqlCommand(GrantStatement(table, role), connection, transaction))
                    await grant.ExecuteNonQueryAsync(ct);
                // Effective privileges include PUBLIC and column grants; ACL text alone is insufficient.
                foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER", "MAINTAIN" })
                {
                    var columnPrivilege = privilege is "SELECT" or "INSERT" or "UPDATE" or "REFERENCES";
                    var query = columnPrivilege
                        ? "SELECT has_table_privilege(@role, @relation, @privilege), has_table_privilege(@role, @relation, @grant), has_any_column_privilege(@role, @relation, @privilege), has_any_column_privilege(@role, @relation, @grant)"
                        : "SELECT has_table_privilege(@role, @relation, @privilege), has_table_privilege(@role, @relation, @grant), false, false";
                    await using var verify = new NpgsqlCommand(query, connection, transaction);
                    verify.Parameters.AddWithValue("role", role);
                    verify.Parameters.AddWithValue("relation", "public." + Quote(table));
                    verify.Parameters.AddWithValue("privilege", privilege);
                    verify.Parameters.AddWithValue("grant", privilege + " WITH GRANT OPTION");
                    await using var reader = await verify.ExecuteReaderAsync(ct);
                    await reader.ReadAsync(ct);
                    var expected = allowed.Split(", ").Contains(privilege, StringComparer.Ordinal);
                    if (reader.GetBoolean(0) != expected || reader.GetBoolean(1) || (!expected && reader.GetBoolean(2)) || reader.GetBoolean(3))
                        throw new DeploymentPolicyException("Unexpected effective runtime privileges remain; review PUBLIC and column grants before deployment.");
                }
            }
        }
        await transaction.CommitAsync(ct);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
