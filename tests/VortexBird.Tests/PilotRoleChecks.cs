using System.Text.Json;
using VortexBird.Infrastructure;
using VortexBird.Infrastructure.Deployment;

internal static class PilotRoleChecks
{
    private const string Issuer = "https://cognito-idp.us-east-2.amazonaws.com/us-east-2_nLop817zD";
    private const string Batch = "0123456789abcdef0123456789abcdef";
    private static readonly string[] Roles = ["SUPER_ADMIN", "ADMIN", "ARCHITECT", "INTEGRATION_MANAGER", "OPERATIONS", "SECURITY_AUDITOR", "PARTNER_ADMIN", "PARTNER_USER", "VIEWER"];

    private sealed record Account(string Role, string Subject, string Email, string? DisplayName = null, string? Replaces = null);

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var catalog = DemoSeed.PermissionNames;
        var readNames = catalog.Where(name => name.EndsWith(".read", StringComparison.Ordinal)).ToArray();

        // The two administrative roles keep the full catalog; every other role is read-only no more.
        check(PilotRolePermissions.Expected("SUPER_ADMIN", catalog).SequenceEqual(catalog), "SUPER_ADMIN keeps the full permission catalog");
        check(PilotRolePermissions.Expected("ADMIN", catalog).SequenceEqual(catalog), "ADMIN keeps the full permission catalog");
        check(PilotRolePermissions.Expected("VIEWER", catalog).SequenceEqual(readNames), "VIEWER remains the read-only role");

        var operational = new[] { "ARCHITECT", "INTEGRATION_MANAGER", "OPERATIONS", "SECURITY_AUDITOR", "PARTNER_ADMIN", "PARTNER_USER" };
        var sets = operational.Select(role => (Role: role, Set: PilotRolePermissions.Expected(role, catalog).ToHashSet())).ToArray();
        check(sets.All(entry => entry.Set.Count > 0 && entry.Set.IsSubsetOf(catalog)), "Every operational role owns a defined subset of the catalog");
        check(sets.Select(entry => string.Join('|', entry.Set.OrderBy(name => name, StringComparer.Ordinal))).Distinct(StringComparer.Ordinal).Count() == sets.Length,
            "Operational roles no longer share one identical permission set");
        check(sets.Single(entry => entry.Role == "OPERATIONS").Set.Contains("orders.create"), "OPERATIONS can create orders");
        check(!sets.Single(entry => entry.Role == "PARTNER_USER").Set.Contains("orders.cancel"), "PARTNER_USER cannot cancel orders");
        check(!sets.Single(entry => entry.Role == "ARCHITECT").Set.Contains("events.manage"), "ARCHITECT observes events without replay rights");
        check(sets.Single(entry => entry.Role == "INTEGRATION_MANAGER").Set.Contains("events.manage"), "INTEGRATION_MANAGER owns event replay and DLQ handling");
        var auditor = sets.Single(entry => entry.Role == "SECURITY_AUDITOR").Set;
        check(auditor.Contains("activity_logs.read") && !auditor.Contains("orders.cancel"), "SECURITY_AUDITOR reads the audit trail without operational write rights");

        // Rebalance: grant what the role is missing, revoke what it must no longer have.
        var catalogIds = catalog.Select(name => (name, Guid.NewGuid())).ToArray();
        var (grant, revoke) = PilotRolePermissions.Rebalance("OPERATIONS", [], catalogIds);
        check(grant.Length == PilotRolePermissions.Expected("OPERATIONS", catalog).Length && revoke.Length == 0, "Empty baseline grants the whole OPERATIONS set");
        var usersDelete = catalogIds.Single(entry => entry.name == "users.delete");
        var (_, revokeExtra) = PilotRolePermissions.Rebalance("OPERATIONS", [usersDelete.Item2], catalogIds);
        check(revokeExtra.SequenceEqual([usersDelete.Item2]), "Rebalance revokes permissions outside the role matrix");

        var parsed = Read([.. Roles.Select(OriginalAccount)], [.. Roles.Select(role => TestAccount(role, TestEmail(role), TestName(role)))]);
        check(parsed.TestAccounts.Length == 9, "Manifest v2 accepts one friendly test account per pilot role");
        check(parsed.TestAccounts.All(identity => identity.DisplayName.EndsWith("Prueba", StringComparison.Ordinal) && char.IsUpper(identity.DisplayName[0]) && !identity.DisplayName.Contains('_')),
            "Test account display names stay simple and independent of the role slug");

        reject(() => Read([.. Roles.Select(OriginalAccount)], [
            TestAccount("VIEWER", "visualizadorPrueba@pilot.vortexbird.invalid", "visualizadorPrueba"),
            TestAccount("VIEWER", "otraPrueba@pilot.vortexbird.invalid", "otraPrueba"),
        ]), "Reject a repeated test role");
        var pilotSubject = OriginalAccount(Roles.First());
        reject(() => Read([.. Roles.Select(OriginalAccount)], [TestAccount(Roles.First(), "copiaPrueba@pilot.vortexbird.invalid", "copiaPrueba", pilotSubject.Subject)]),
            "Reject a test subject reused from the pilot batch");
        reject(() => Read([.. Roles.Select(OriginalAccount)], [TestAccount("OPERATIONS", "ab@pilot.vortexbird.invalid", "ab")]), "Reject display names that are not simple names");
        reject(() => Read([.. Roles.Select(OriginalAccount)], [TestAccount("DEVELOPER", "desarrolladorPrueba@pilot.vortexbird.invalid", "desarrolladorPrueba")]),
            "Reject test roles outside the pilot catalog");
        reject(() => Read([.. Roles.Select(OriginalAccount)], [TestAccount("OPERATIONS", "vb-super-admin-01234567@pilot.vortexbird.invalid", "superadminPrueba")]),
            "Reject a test login colliding with a pilot account");
        reject(() => Read([.. Roles.Select(OriginalAccount)], [TestAccount("OPERATIONS", "OperacionesPrueba@pilot.vortexbird.invalid", "operacionesPrueba")]),
            "Reject non-lowercase test logins");
        reject(() => Read([.. Roles.Select(OriginalAccount)], [TestAccount("OPERATIONS", "operaciones@prueba.com", "operacionesPrueba", replaces: OriginalAccount("SUPER_ADMIN").Email)]),
            "Reject rotating a protected pilot login");
        reject(() => Read([.. Roles.Select(OriginalAccount)], [TestAccount("OPERATIONS", "operaciones@prueba.com", "operacionesPrueba", replaces: "not-an-email")]),
            "Reject a malformed replaced login");
        reject(() => Read([.. Roles.Select(OriginalAccount)], [TestAccount("OPERATIONS", "operaciones@prueba.com", "operacionesPrueba", replaces: OriginalAccount("ADMIN").Email)]),
            "Reject replacing a pilot account by email");
        var rotated = Read([.. Roles.Select(OriginalAccount)], [TestAccount("OPERATIONS", "operaciones@prueba.com", "operacionesPrueba", replaces: "operacionesprueba@pilot.vortexbird.invalid")]);
        check(rotated.TestAccounts.Single(identity => identity.Role == "OPERATIONS").Replaces == "operacionesprueba@pilot.vortexbird.invalid",
            "Manifest v2 accepts a replaced login for test account rotation");
        reject(() => Read([]), "Reject a manifest without the nine pilot accounts");
    }

    private static Account OriginalAccount(string role) => new(role, Guid.NewGuid().ToString("D"),
        $"vb-{role.Replace('_', '-').ToLowerInvariant()}-{Batch[..8]}@pilot.vortexbird.invalid");

    private static string TestEmail(string role) => $"{role.ToLowerInvariant()}prueba@pilot.vortexbird.invalid";

    private static string TestName(string role) => string.Concat(role.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant())) + "Prueba";

    private static Account TestAccount(string role, string email, string displayName, string? subject = null, string? replaces = null) =>
        new(role, subject ?? Guid.NewGuid().ToString("D"), email, displayName, replaces);

    private static global::VortexBird.API.PilotRoleBootstrap Read(Account[] accounts, Account[]? testAccounts = null)
    {
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            issuer = Issuer,
            batch = Batch,
            accounts = accounts.Select(account => (object)new { role = account.Role, subject = account.Subject, email = account.Email }),
            testAccounts = testAccounts?.Select(account => (object)(account.Replaces is null
                ? new { role = account.Role, subject = account.Subject, email = account.Email, displayName = account.DisplayName }
                : new { role = account.Role, subject = account.Subject, email = account.Email, displayName = account.DisplayName, replaces = account.Replaces })),
        });
        using var document = JsonDocument.Parse(manifest);
        return global::VortexBird.API.PilotRoleBootstrap.Read(document.RootElement.Clone(), Issuer);
    }
}
