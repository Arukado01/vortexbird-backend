using Microsoft.EntityFrameworkCore;
using VortexBird.Domain.Entities;

namespace VortexBird.Infrastructure.Deployment;

public static class InitialAdminBootstrap
{
    public static void ValidateExisting(InitialAdministrator requested, User? user, Role? role, string[] administratorSubjects, bool anyUsers)
    {
        if (role is not null && !role.Protected)
            throw new DeploymentPolicyException("An unprotected SUPER_ADMIN role already exists; explicit administrative repair is required.");
        if (administratorSubjects.Any(subject => subject != requested.Subject))
            throw new DeploymentPolicyException("Another protected administrator already exists; bootstrap cannot add or replace an administrator.");
        if (user is null)
        {
            if (anyUsers) throw new DeploymentPolicyException("Bootstrap only creates the first identity in an empty Users table.");
            return;
        }
        if (!administratorSubjects.Contains(requested.Subject) || !user.Enabled || user.PartnerId.HasValue
            || user.Subject != requested.Subject || !string.Equals(user.Email, requested.Email, StringComparison.OrdinalIgnoreCase))
            throw new DeploymentPolicyException("Existing identity differs from the requested enabled global administrator; bootstrap will not promote, reactivate or relink it.");
    }

    public static async Task Run(IntegrationDbContext db, InitialAdministrator requested, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('vortexbird:initial-administrator', 0))", ct);
        var role = await db.Roles.SingleOrDefaultAsync(x => x.Name == "SUPER_ADMIN", ct);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Subject == requested.Subject, ct);
        var administrators = await (from assignment in db.UserRoles join candidateRole in db.Roles on assignment.RoleId equals candidateRole.Id
            join candidateUser in db.Users on assignment.UserId equals candidateUser.Id
            where candidateRole.Protected || candidateRole.Name == "SUPER_ADMIN" select candidateUser.Subject).Distinct().ToArrayAsync(ct);
        ValidateExisting(requested, user, role, administrators, await db.Users.AnyAsync(ct));
        if (await db.Users.AnyAsync(x => x.Email == requested.Email && x.Subject != requested.Subject, ct))
            throw new DeploymentPolicyException("The administrator email is already bound to another OIDC subject.");
        if (role is null)
        {
            role = new Role { Name = "SUPER_ADMIN", Description = "Administrador inicial OIDC", Protected = true };
            db.Roles.Add(role);
        }
        var permissions = await db.Permissions.ToListAsync(ct);
        foreach (var name in DemoSeed.PermissionNames)
        {
            if (permissions.Any(x => x.Name == name)) continue;
            var permission = new Permission { Name = name, Module = name.Split('.')[0] };
            permissions.Add(permission); db.Permissions.Add(permission);
        }
        var assigned = await db.RolePermissions.Where(x => x.RoleId == role.Id).Select(x => x.PermissionId).ToListAsync(ct);
        foreach (var permission in permissions.Where(x => DemoSeed.PermissionNames.Contains(x.Name) && !assigned.Contains(x.Id)))
            db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
        if (user is null)
        {
            user = new User { Subject = requested.Subject, Email = requested.Email, DisplayName = requested.DisplayName, Enabled = true };
            db.Users.Add(user);
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            db.ActivityLogs.Add(new ActivityLog { UserId = "deployment-job", Module = "users", Action = "bootstrap_admin", EntityId = user.Id.ToString(),
                Description = "Administrador global inicial vinculado a subject OIDC explícito; sin credenciales locales.", CorrelationId = Guid.NewGuid().ToString("N") });
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
