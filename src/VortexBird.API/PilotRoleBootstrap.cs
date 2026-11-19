using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VortexBird.Domain.Entities;
using VortexBird.Infrastructure;
using VortexBird.Infrastructure.Deployment;

namespace VortexBird.API;

internal sealed record PilotRoleBootstrap(string Batch, PilotRoleBootstrap.Identity[] Accounts, PilotRoleBootstrap.Identity[] TestAccounts)
{
    private static readonly StringComparison OrdinalIgnoreCase = StringComparison.OrdinalIgnoreCase;
    internal sealed record Identity(string Role, string Subject, string Email, string DisplayName = "", string Replaces = "");
    private static readonly string[] Catalog =
    [
        "SUPER_ADMIN", "ADMIN", "ARCHITECT", "INTEGRATION_MANAGER", "OPERATIONS",
        "SECURITY_AUDITOR", "PARTNER_ADMIN", "PARTNER_USER", "VIEWER"
    ];

    public Identity Administrator => Accounts.Single(identity => identity.Role == "SUPER_ADMIN");

    /// <summary>Expected permission names for a role, including the full-catalog and read-only defaults.</summary>
    public static string[] ExpectedPermissions(string role, string[] catalog) => PilotRolePermissions.Expected(role, catalog);

    /// <summary>Permission grant/revoke delta between the stored grants and the expected matrix.</summary>
    public static (Guid[] Grant, Guid[] Revoke) Rebalance(string role, Guid[] currentIds, Permission[] catalog) =>
        PilotRolePermissions.Rebalance(role, currentIds, [.. catalog.Select(permission => (permission.Name, permission.Id))]);

    public static PilotRoleBootstrap Read(JsonElement input, string issuer)
    {
        if (!string.Equals(input.GetProperty("issuer").GetString(), issuer, StringComparison.Ordinal) ||
            !CognitoAuthentication.IsUserPoolIssuer(issuer))
            throw new InvalidOperationException("Unrecognized Cognito pilot issuer.");
        var batch = input.GetProperty("batch").GetString() ?? "";
        if (!Regex.IsMatch(batch, "^[a-f0-9]{32}$")) throw new InvalidOperationException("Invalid pilot batch.");
        var items = input.GetProperty("accounts").EnumerateArray().Select(x => new Identity(
            x.GetProperty("role").GetString() ?? "", x.GetProperty("subject").GetString() ?? "",
            x.GetProperty("email").GetString() ?? "")).ToArray();
        if (!Catalog.ToHashSet(StringComparer.Ordinal).SetEquals(items.Select(x => x.Role)) ||
            items.Length != Catalog.Length || items.Select(x => x.Subject).Distinct(StringComparer.Ordinal).Count() != Catalog.Length)
            throw new InvalidOperationException("Expected exactly one distinct identity for each pilot role.");
        foreach (var item in items)
        {
            var login = $"vb-{item.Role.Replace('_', '-').ToLowerInvariant()}-{batch[..8]}@pilot.vortexbird.invalid";
            if (item.Email != login || !Guid.TryParseExact(item.Subject, "D", out var parsed) ||
                parsed == Guid.Empty || parsed.ToString("D") != item.Subject)
                throw new InvalidOperationException("Pilot subject or login is invalid.");
        }
        // Optional v2 accounts: friendly, per-role test identities created outside the protected batch.
        var testAccounts = input.TryGetProperty("testAccounts", out var tests)
            ? tests.EnumerateArray().Select(x => new Identity(
                x.GetProperty("role").GetString() ?? "", x.GetProperty("subject").GetString() ?? "",
                x.GetProperty("email").GetString() ?? "", x.GetProperty("displayName").GetString() ?? "",
                x.TryGetProperty("replaces", out var replaces) ? replaces.GetString() ?? "" : "")).ToArray()
            : [];
        ValidateTestAccounts(items, testAccounts);
        return new(batch, items, testAccounts);
    }

    private static void ValidateTestAccounts(Identity[] pilot, Identity[] testAccounts)
    {
        if (testAccounts.Length > Catalog.Length) throw new InvalidOperationException("At most one test account per pilot role.");
        var pilotSubjects = pilot.Select(x => x.Subject).ToHashSet(StringComparer.Ordinal);
        var pilotEmails = pilot.Select(x => x.Email).ToHashSet(StringComparer.Ordinal);
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        var emails = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in testAccounts)
        {
            if (!Catalog.Contains(item.Role) || !roles.Add(item.Role))
                throw new InvalidOperationException("Test accounts must map one distinct pilot role each.");
            if (!Guid.TryParseExact(item.Subject, "D", out var subject) || subject == Guid.Empty ||
                item.Subject != subject.ToString("D") || !subjects.Add(item.Subject) || pilotSubjects.Contains(item.Subject))
                throw new InvalidOperationException("Test account subjects must be distinct GUIDs outside the pilot batch.");
            if (!EmailIsValid(item.Email) || !emails.Add(item.Email) || pilotEmails.Contains(item.Email))
                throw new InvalidOperationException("Test account emails must be unique pilot logins.");
            if (!Regex.IsMatch(item.DisplayName, "^[A-Za-z][A-Za-z0-9]{3,31}$"))
                throw new InvalidOperationException("Test account display names must be simple alphanumeric names.");
            if (item.Replaces.Length > 0 && (!EmailIsValid(item.Replaces) || pilotEmails.Contains(item.Replaces)))
                throw new InvalidOperationException("A replaced test login must be a plain email outside the pilot batch.");
        }
    }

    private static bool EmailIsValid(string value) =>
        Regex.IsMatch(value, "^[a-z0-9][a-z0-9.+_-]*@[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$");

    public async Task Run(string connection, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<IntegrationDbContext>().UseNpgsql(connection).Options;
        await using var db = new IntegrationDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('vortexbird:initial-administrator', 0))", token);
        var primary = Administrator;
        var privileged = await (from ur in db.UserRoles
                                join r in db.Roles on ur.RoleId equals r.Id
                                join u in db.Users on ur.UserId equals u.Id
                                where r.Protected || r.Name == "SUPER_ADMIN"
                                select new { u.Subject, u.Email, u.Enabled, u.PartnerId, r.Name, r.Protected }).ToListAsync(token);
        // The protected role is reserved for the bootstrap administrator plus, at most, the one
        // SUPER_ADMIN test account declared by the manifest; nothing undeclared may hold it.
        // A rotating SUPER_ADMIN test account may still carry its previous login at this point:
        // the relink below repurposes that exact row inside this transaction.
        var declaredPrimaryTests = TestAccounts.Where(x => x.Role == "SUPER_ADMIN").ToArray();
        var primaryBinding = privileged.SingleOrDefault(x => x.Subject == primary.Subject);
        var declaredExtras = privileged.Where(x => x.Subject != primary.Subject).ToArray();
        var declaredMatch = declaredExtras.Length == declaredPrimaryTests.Length && declaredPrimaryTests.All(item =>
            declaredExtras.Any(x => x.Enabled && !x.PartnerId.HasValue && x.Name == "SUPER_ADMIN" && x.Protected &&
                ((x.Subject == item.Subject && x.Email == item.Email) ||
                 (item.Replaces.Length > 0 && string.Equals(x.Email, item.Replaces, StringComparison.OrdinalIgnoreCase)))));
        if (primaryBinding is null || declaredExtras.Length != declaredPrimaryTests.Length || !declaredMatch)
            throw new InvalidOperationException("Run the protected initial administrator bootstrap for this exact Cognito identity.");

        var partnerCode = $"vb-cognito-{Batch[..12]}";
        var partnerName = $"VortexBird piloto {Batch[..8]}";
        var partner = await db.Partners.SingleOrDefaultAsync(x => x.Code == partnerCode, token);
        if (partner is null)
        {
            partner = new Partner { Code = partnerCode, Name = partnerName, Enabled = true, RequestsPerMinute = 120 };
            db.Partners.Add(partner);
        }
        else if (!partner.Enabled || partner.Name != partnerName)
            throw new InvalidOperationException("Existing pilot tenant differs from the requested tenant.");

        var supportedPermissions = DemoSeed.PermissionNames;
        var permissions = await db.Permissions.Where(x => supportedPermissions.Contains(x.Name)).ToListAsync(token);
        if (permissions.Count != supportedPermissions.Length)
            throw new InvalidOperationException("The protected bootstrap must initialize the permission catalog first.");

        foreach (var item in Accounts)
        {
            var role = await EnsureRole(db, item.Role, token);
            Guid? partnerId = item.Role.StartsWith("PARTNER_", StringComparison.Ordinal) ? partner.Id : null;
            var matches = await db.Users.Where(x => x.Subject == item.Subject || x.Email == item.Email).ToListAsync(token);
            if (matches.Count == 0)
            {
                if (item.Role == "SUPER_ADMIN") throw new InvalidOperationException("Protected administrator binding missing.");
                var user = new User { Subject = item.Subject, Email = item.Email, DisplayName = $"Piloto {item.Role}", Enabled = true, PartnerId = partnerId };
                db.Users.Add(user);
                db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            }
            else
            {
                if (matches.Count != 1 || matches[0].Subject != item.Subject || matches[0].Email != item.Email ||
                    !matches[0].Enabled || matches[0].PartnerId != partnerId)
                    throw new InvalidOperationException("An existing user differs from the proposed subject or tenant.");
                var id = matches[0].Id;
                var grants = await db.UserRoles.Where(x => x.UserId == id).Select(x => x.RoleId).ToListAsync(token);
                if (grants.Count != 1 || grants[0] != role.Id) throw new InvalidOperationException("An existing pilot account has a conflicting role.");
            }
        }

        var rebalanced = 0;
        foreach (var role in await db.Roles.Where(x => Catalog.Contains(x.Name)).ToListAsync(token))
        {
            var current = await db.RolePermissions.Where(x => x.RoleId == role.Id).Select(x => x.PermissionId).ToArrayAsync(token);
            var (grant, revoke) = Rebalance(role.Name, current, [.. permissions]);
            if (grant.Length == 0 && revoke.Length == 0) continue;
            db.RolePermissions.AddRange(grant.Select(id => new RolePermission { RoleId = role.Id, PermissionId = id }));
            var remove = revoke.ToHashSet();
            foreach (var entry in await db.RolePermissions.Where(x => x.RoleId == role.Id && remove.Contains(x.PermissionId)).ToListAsync(token))
                db.RolePermissions.Remove(entry);
            rebalanced++;
        }

        var bound = 0;
        var relinked = 0;
        foreach (var item in TestAccounts)
        {
            var role = await db.Roles.SingleAsync(x => x.Name == item.Role, token);
            Guid? partnerId = item.Role.StartsWith("PARTNER_", StringComparison.Ordinal) ? partner.Id : null;
            var user = await db.Users.SingleOrDefaultAsync(x => x.Subject == item.Subject || x.Email == item.Email, token);
            if (user is null && item.Replaces.Length > 0)
            {
                // Rotation: rebind the superseded test identity in place so its id, role grant
                // and tenant scope survive the Cognito account swap. Pilot accounts never rotate.
                var replaced = await db.Users.SingleOrDefaultAsync(x => x.Email == item.Replaces, token);
                if (replaced is not null && !Accounts.Any(x => string.Equals(x.Email, item.Replaces, OrdinalIgnoreCase)))
                {
                    replaced.Subject = item.Subject;
                    replaced.Email = item.Email;
                    user = replaced;
                    relinked++;
                }
            }
            if (user is null)
            {
                user = new User { Subject = item.Subject, Email = item.Email, DisplayName = item.DisplayName, Enabled = true, PartnerId = partnerId };
                db.Users.Add(user);
                db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
                bound++;
            }
            else
            {
                if (user.Subject != item.Subject || user.Email != item.Email || user.PartnerId != partnerId)
                    throw new InvalidOperationException("An existing test account differs from the proposed subject or tenant.");
                if (!user.Enabled) throw new InvalidOperationException("A disabled test account must be re-enabled explicitly.");
                if (user.DisplayName != item.DisplayName) user.DisplayName = item.DisplayName;
                var grants = await db.UserRoles.Where(x => x.UserId == user.Id).Select(x => x.RoleId).ToListAsync(token);
                if (grants.Count != 1 || grants[0] != role.Id) throw new InvalidOperationException("An existing test account has a conflicting role.");
            }
        }

        if (db.ChangeTracker.HasChanges())
        {
            db.ActivityLogs.Add(new ActivityLog { UserId = "deployment-job", Module = "users",
                Action = relinked > 0 ? "relink_pilot_test_accounts" : TestAccounts.Length > 0 ? "bind_pilot_and_test_accounts" : "bind_cognito_pilot",
                EntityId = Batch, Description = $"Catálogo de permisos por rol actualizado; {TestAccounts.Length} cuentas de prueba vinculadas; {relinked} logins rotados; {rebalanced} roles rebalanceados.",
                CorrelationId = Guid.NewGuid().ToString("N") });
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    private static async Task<Role> EnsureRole(IntegrationDbContext db, string role, CancellationToken token)
    {
        var existing = await db.Roles.SingleOrDefaultAsync(x => x.Name == role, token);
        if (existing is not null) return existing;
        if (role == "SUPER_ADMIN") throw new InvalidOperationException("Protected administrator role missing.");
        var created = new Role { Name = role, Description = $"Rol del piloto VortexBird {role}" };
        db.Roles.Add(created);
        return created;
    }
}
