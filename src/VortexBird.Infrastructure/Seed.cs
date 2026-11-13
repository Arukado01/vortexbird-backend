using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VortexBird.Domain.Entities;

namespace VortexBird.Infrastructure;

public static class DemoSeed
{
    public static readonly Guid PartnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid ChannelId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid AdminId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly string[] PermissionNames = ["users.read","users.create","users.update","users.delete","roles.read","roles.create","roles.update","roles.delete","roles.assign_permissions","permissions.read","permissions.create","partners.read","partners.create","partners.update","partners.manage","channels.read","channels.manage","products.read","products.manage","inventory.read","inventory.manage","pricing.read","pricing.manage","orders.read","orders.create","orders.update","orders.cancel","payments.read","payments.manage","integrations.read","integrations.manage","webhooks.read","webhooks.manage","events.read","events.manage","activity_logs.read","dashboard.read","reports.read","settings.read","settings.manage"];

    public static async Task Run(IntegrationDbContext db, IConfiguration config, CancellationToken ct = default)
    {
        if (await db.Partners.AnyAsync(ct)) return;
        db.Partners.Add(new Partner { Id = PartnerId, Name = "AndesRetail Colombia", Code = "andes-retail", Enabled = true, RequestsPerMinute = 300 });
        db.Channels.Add(new Channel { Id = ChannelId, PartnerId = PartnerId, Name = "E-commerce", Code = "web" });
        var marketplace = new Channel { Id = Guid.NewGuid(), PartnerId = PartnerId, Name = "Marketplace", Code = "marketplace" };
        db.Channels.Add(marketplace);
        for (var i = 1; i <= 30; i++) db.Customers.Add(new Customer { PartnerId = PartnerId, ExternalId = $"CUST-{i:000}", DisplayName = $"Cliente demostración {i:00}" });
        db.Customers.Add(new Customer { PartnerId = PartnerId, ExternalId = "CI-CUSTOMER", DisplayName = "Cliente integración CI" });
        string[] categories = ["Tecnología", "Hogar", "Deportes"];
        for (var i = 1; i <= 48; i++)
        {
            var sku = $"AND-{i:0000}";
            db.Products.Add(new Product { Id = Guid.NewGuid(), Sku = sku, Name = $"{new[] { "Audífonos", "Lámpara", "Mochila", "Teclado", "Termo", "Monitor" }[(i - 1) % 6]} Andes {i:00}", Category = categories[(i - 1) % 3], Active = true });
            foreach (var channel in new[] { ChannelId, marketplace.Id })
            {
                db.Stocks.Add(new Stock { Id = Guid.NewGuid(), PartnerId = PartnerId, ChannelId = channel, StoreId = "BOG-01", Sku = sku, OnHand = 30 + i * 3, Reserved = 0, Version = 1 });
                db.Prices.Add(new Price { Id = Guid.NewGuid(), PartnerId = PartnerId, ChannelId = channel, Sku = sku, Amount = 29900 + i * 5000, Currency = "COP" });
            }
        }
        db.Users.Add(new User { Id = AdminId, Subject = config["Seed:AdminSubject"] ?? "demo-admin", Email = config["Demo:Email"] ?? "admin@andesretail.local", DisplayName = "Administrador AndesRetail", Enabled = true });
        var permissions = PermissionNames.Select(n => new Permission { Id = Guid.NewGuid(), Name = n, Module = n.Split('.')[0] }).ToList();
        db.Permissions.AddRange(permissions);
        foreach (var name in new[] { "SUPER_ADMIN", "ADMIN", "ARCHITECT", "INTEGRATION_MANAGER", "OPERATIONS", "SECURITY_AUDITOR", "PARTNER_ADMIN", "PARTNER_USER", "VIEWER" })
        {
            var role = new Role { Id = Guid.NewGuid(), Name = name, Description = $"Rol inicial {name}", Protected = name == "SUPER_ADMIN" };
            db.Roles.Add(role);
            var allowed = name is "SUPER_ADMIN" or "ADMIN" ? permissions : permissions.Where(x => x.Name.EndsWith(".read")).ToList();
            foreach (var permission in allowed) db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
            if (name == "SUPER_ADMIN") db.UserRoles.Add(new UserRole { RoleId = role.Id, UserId = AdminId });
        }
        foreach (var system in new[] { "ERP", "WMS", "OMS", "CRM", "E-commerce", "Marketplace", "Payment Gateway" })
            db.Integrations.Add(new Integration { Id = Guid.NewGuid(), Name = $"AndesRetail {system}", System = system, Mode = "simulated", Status = "healthy", FailureMode = "none", LatencyMs = 0 });
        db.ActivityLogs.Add(new ActivityLog { Id = Guid.NewGuid(), UserId = "system", Action = "seed", Module = "settings", EntityId = "andesretail", Result = "success", Description = "Datos de demostración iniciales; sistemas externos simulados.", CorrelationId = Guid.NewGuid().ToString(), CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
    }
}
