namespace VortexBird.Infrastructure.Deployment;

/// <summary>
/// Role-specific operating permissions for the Cognito pilot accounts. SUPER_ADMIN and ADMIN keep
/// the full catalog; every other role owns a distinct subset aligned to its responsibility,
/// replacing the previous all-identical read-only baseline.
/// </summary>
public static class PilotRolePermissions
{
    private static readonly Dictionary<string, string[]> Specific = new(StringComparer.Ordinal)
    {
        // Designs and connects adapters; observes the event flow without replay rights.
        ["ARCHITECT"] = ["integrations.read", "integrations.manage", "webhooks.read", "webhooks.manage", "events.read", "settings.read", "dashboard.read", "reports.read"],
        // Runs the integration floor: retries and DLQ handling, workspace preferences, audit trail.
        ["INTEGRATION_MANAGER"] = ["integrations.read", "events.read", "events.manage", "webhooks.read", "activity_logs.read", "settings.read", "settings.manage", "dashboard.read", "reports.read"],
        // Daily operation floor: order lifecycle, stock, catalog consultation, payment tracking.
        ["OPERATIONS"] = ["orders.read", "orders.create", "orders.update", "orders.cancel", "inventory.read", "inventory.manage", "products.read", "pricing.read", "payments.read", "dashboard.read", "reports.read"],
        // Audit-first: identity and permission inspection plus the full operational trail.
        ["SECURITY_AUDITOR"] = ["users.read", "roles.read", "permissions.read", "activity_logs.read", "events.read", "orders.read", "payments.read", "integrations.read", "webhooks.read", "dashboard.read", "reports.read"],
        // Own-tenant administration: catalog, stock and pricing within the partner scope.
        ["PARTNER_ADMIN"] = ["orders.read", "orders.create", "products.read", "products.manage", "inventory.read", "inventory.manage", "pricing.read", "pricing.manage", "payments.read", "channels.read", "dashboard.read", "reports.read"],
        // Own-tenant operator: create orders and follow their state.
        ["PARTNER_USER"] = ["orders.read", "orders.create", "products.read", "inventory.read", "payments.read", "dashboard.read"],
    };

    /// <summary>Expected permission names for a role, including the full-catalog and read-only defaults.</summary>
    public static string[] Expected(string role, string[] catalog) => Specific.TryGetValue(role, out var permissions) ? permissions
        : role is "SUPER_ADMIN" or "ADMIN" ? catalog
        : catalog.Where(name => name.EndsWith(".read", StringComparison.Ordinal)).ToArray();

    /// <summary>Permission grant/revoke delta between the stored grants and the expected matrix.</summary>
    public static (Guid[] Grant, Guid[] Revoke) Rebalance(string role, Guid[] currentIds, IReadOnlyCollection<(string Name, Guid Id)> catalog)
    {
        var expected = Expected(role, [.. catalog.Select(entry => entry.Name)]);
        var byName = catalog.ToDictionary(entry => entry.Name, entry => entry.Id, StringComparer.Ordinal);
        var current = currentIds.ToHashSet();
        var grant = expected.Where(name => byName.TryGetValue(name, out var id) && current.Add(id)).Select(name => byName[name]).ToArray();
        var expectedIds = expected.Where(byName.ContainsKey).Select(name => byName[name]).ToHashSet();
        var revoke = currentIds.Where(id => !expectedIds.Contains(id)).ToArray();
        return (grant, revoke);
    }
}
