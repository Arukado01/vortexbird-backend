using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using VortexBird.Application.Abstractions;
using VortexBird.Application.Contracts;
using VortexBird.Domain.Entities;
using VortexBird.Infrastructure;

namespace VortexBird.API;

public static class AdministrationEndpoints
{
    public static RouteGroupBuilder MapAdministrationEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/partners", async (HttpRequest request, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("partners.read", ct);
            var query = db.Partners.AsNoTracking();
            var partnerId = AdminPage.Partner(request, actor);
            if (partnerId.HasValue) query = query.Where(x => x.Id == partnerId);
            var search = AdminPage.Search(request);
            if (search is not null) query = query.Where(x => x.Name.ToLower().Contains(search) || x.Code.ToLower().Contains(search));
            return await AdminPage.Read(query.OrderBy(x => x.Name).ThenBy(x => x.Id), request, ct);
        });
        api.MapPost("/partners", async (PartnerInput input, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("partners.create", ct); actor.RequireGlobal();
            var entity = new Partner(); SetPartner(entity, input); db.Partners.Add(entity);
            await db.SaveChangesAsync(ct); return Results.Created($"/api/v1/partners/{entity.Id}", entity);
        });
        api.MapPut("/partners/{id:guid}", async (Guid id, PartnerInput input, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("partners.update", ct); actor.RequireGlobal();
            var entity = await db.Partners.FindAsync([id], ct) ?? throw new MissingEntityException("Partner no encontrado.");
            SetPartner(entity, input); await db.SaveChangesAsync(ct); return Results.Ok(entity);
        });
        api.MapDelete("/partners/{id:guid}", async (Guid id, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("partners.manage", ct); actor.RequireGlobal();
            var entity = await db.Partners.FindAsync([id], ct) ?? throw new MissingEntityException("Partner no encontrado.");
            entity.Enabled = false; await db.SaveChangesAsync(ct); return Results.NoContent();
        });

        api.MapGet("/channels", async (HttpRequest request, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("channels.read", ct);
            var query = db.Channels.AsNoTracking();
            var partnerId = AdminPage.Partner(request, actor);
            if (partnerId.HasValue) query = query.Where(x => x.PartnerId == partnerId);
            var selectedPartner = AdminPage.GuidFilter(request, "partnerId");
            if (selectedPartner.HasValue) { await actor.EnsurePartnerAccess(selectedPartner.Value, cancellationToken: ct); query = query.Where(x => x.PartnerId == selectedPartner); }
            var claimedChannels = AdminPage.ClaimedChannels(request);
            if (claimedChannels.Length > 0) query = query.Where(x => claimedChannels.Contains(x.Id));
            var search = AdminPage.Search(request);
            if (search is not null) query = query.Where(x => x.Name.ToLower().Contains(search) || x.Code.ToLower().Contains(search));
            return await AdminPage.Read(query.OrderBy(x => x.Name).ThenBy(x => x.Id), request, ct);
        });
        api.MapPost("/channels", async (ChannelInput input, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            actor.RequireUnrestrictedChannel();
            await actor.Require("channels.manage", ct); await actor.EnsurePartnerAccess(input.PartnerId, cancellationToken: ct);
            var entity = new Channel { PartnerId = input.PartnerId }; SetChannel(entity, input); db.Channels.Add(entity);
            await db.SaveChangesAsync(ct); return Results.Created($"/api/v1/channels/{entity.Id}", entity);
        });
        api.MapPut("/channels/{id:guid}", async (Guid id, ChannelInput input, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("channels.manage", ct);
            var entity = await db.Channels.FindAsync([id], ct) ?? throw new MissingEntityException("Canal no encontrado.");
            await actor.EnsurePartnerAccess(entity.PartnerId, entity.Id, ct);
            if (input.PartnerId != entity.PartnerId) throw new RequestInputException("No se puede transferir un canal a otro partner.");
            SetChannel(entity, input); await db.SaveChangesAsync(ct); return Results.Ok(entity);
        });
        api.MapDelete("/channels/{id:guid}", async (Guid id, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("channels.manage", ct);
            var entity = await db.Channels.FindAsync([id], ct) ?? throw new MissingEntityException("Canal no encontrado.");
            await actor.EnsurePartnerAccess(entity.PartnerId, entity.Id, ct);
            entity.Enabled = false; await db.SaveChangesAsync(ct); return Results.NoContent();
        });

        api.MapGet("/products", async (HttpRequest request, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("products.read", ct);
            var query = db.Products.AsNoTracking();
            var partnerId = AdminPage.Partner(request, actor);
            var channels = AdminPage.ClaimedChannels(request);
            if (partnerId.HasValue) query = query.Where(x => db.Stocks.Any(s => s.Sku == x.Sku && s.PartnerId == partnerId && (channels.Length == 0 || channels.Contains(s.ChannelId))));
            else if (channels.Length > 0) query = query.Where(x => db.Stocks.Any(s => s.Sku == x.Sku && channels.Contains(s.ChannelId)));
            var search = AdminPage.Search(request);
            if (search is not null) query = query.Where(x => x.Name.ToLower().Contains(search) || x.Sku.ToLower().Contains(search));
            return await AdminPage.Read(query.OrderBy(x => x.Sku), request, ct);
        });
        api.MapPost("/products", async (ProductInput input, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("products.manage", ct); actor.RequireGlobal();
            var entity = new Product(); SetProduct(entity, input); db.Products.Add(entity);
            await db.SaveChangesAsync(ct); return Results.Created($"/api/v1/products/{entity.Id}", entity);
        });
        api.MapPut("/products/{id:guid}", async (Guid id, ProductInput input, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("products.manage", ct); actor.RequireGlobal();
            var entity = await db.Products.FindAsync([id], ct) ?? throw new MissingEntityException("Producto no encontrado.");
            if (entity.Sku != SafeInput.Code(input.Sku, "sku")) throw new RequestInputException("SKU es inmutable para preservar las referencias de inventario y órdenes.");
            SetProduct(entity, input); await db.SaveChangesAsync(ct); return Results.Ok(entity);
        });
        api.MapDelete("/products/{id:guid}", async (Guid id, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("products.manage", ct); actor.RequireGlobal();
            var entity = await db.Products.FindAsync([id], ct) ?? throw new MissingEntityException("Producto no encontrado.");
            entity.Active = false; await db.SaveChangesAsync(ct); return Results.NoContent();
        });

        api.MapGet("/users", async (HttpRequest request, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("users.read", ct);
            var query = db.Users.AsNoTracking();
            var partner = AdminPage.Partner(request, actor);
            if (partner.HasValue) query = query.Where(x => x.PartnerId == partner);
            var search = AdminPage.Search(request);
            if (search is not null) query = query.Where(x => x.Email.ToLower().Contains(search) || x.DisplayName.ToLower().Contains(search));
            var page = await AdminPage.Read(query.OrderBy(x => x.DisplayName).ThenBy(x => x.Id), request, ct);
            var ids = page.Items.Select(x => x.Id).ToArray();
            var bindings = await db.UserRoles.AsNoTracking().Where(x => ids.Contains(x.UserId)).ToListAsync(ct);
            return new PagedResult<object>(page.Items.Select(x => (object)new { x.Id, x.Subject, x.Email, x.DisplayName, x.Enabled, x.PartnerId, roleIds = bindings.Where(r => r.UserId == x.Id).Select(r => r.RoleId).ToArray() }).ToArray(), page.Page, page.PageSize, page.TotalCount);
        });
        api.MapPost("/users", async (UserInput input, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("users.create", ct); actor.RequireGlobal();
            var entity = new User(); await SetUser(entity, input, db, ct); db.Users.Add(entity);
            if (input.RoleIds is { Length: > 0 }) { await actor.Require("roles.assign_permissions", ct); await AssignUserRoles(entity, input.RoleIds, db, ct); }
            await db.SaveChangesAsync(ct); return Results.Created($"/api/v1/users/{entity.Id}", entity);
        });
        api.MapPut("/users/{id:guid}", async (Guid id, UserInput input, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("users.update", ct); actor.RequireGlobal();
            var entity = await db.Users.FindAsync([id], ct) ?? throw new MissingEntityException("Usuario no encontrado.");
            await GuardProtectedUser(id, db, ct);
            if (actor.User?.Id == id && (!input.Enabled || input.PartnerId.HasValue)) throw new RequestInputException("No puedes desactivar o limitar tu propia cuenta.");
            if (entity.Subject != input.Subject) throw new RequestInputException("El subject OIDC es inmutable.");
            await SetUser(entity, input, db, ct);
            if (input.RoleIds is not null) { await actor.Require("roles.assign_permissions", ct); if (actor.User?.Id == id) throw new RequestInputException("No puedes cambiar tus propios roles."); await AssignUserRoles(entity, input.RoleIds, db, ct); }
            await db.SaveChangesAsync(ct); return Results.Ok(entity);
        });
        api.MapDelete("/users/{id:guid}", async (Guid id, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("users.delete", ct); actor.RequireGlobal();
            if (actor.User?.Id == id) throw new RequestInputException("No puedes desactivar tu propia cuenta.");
            var entity = await db.Users.FindAsync([id], ct) ?? throw new MissingEntityException("Usuario no encontrado.");
            await GuardProtectedUser(id, db, ct); entity.Enabled = false; await db.SaveChangesAsync(ct); return Results.NoContent();
        });
        api.MapPut("/users/{id:guid}/roles", async (Guid id, UserRolesInput input, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("roles.assign_permissions", ct); actor.RequireGlobal();
            if (actor.User?.Id == id) throw new RequestInputException("No puedes cambiar tus propios roles.");
            var entity = await db.Users.FindAsync([id], ct) ?? throw new MissingEntityException("Usuario no encontrado.");
            await GuardProtectedUser(id, db, ct); await AssignUserRoles(entity, input.RoleIds, db, ct); await db.SaveChangesAsync(ct);
            return Results.Ok(new { entity.Id, roleIds = input.RoleIds.Distinct().ToArray() });
        });

        api.MapGet("/roles", async (HttpRequest request, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("roles.read", ct);
            var query = db.Roles.AsNoTracking();
            var search = AdminPage.Search(request);
            if (search is not null) query = query.Where(x => x.Name.ToLower().Contains(search));
            var page = await AdminPage.Read(query.OrderBy(x => x.Name), request, ct);
            var ids = page.Items.Select(x => x.Id).ToArray();
            var grants = await (from rp in db.RolePermissions join p in db.Permissions on rp.PermissionId equals p.Id where ids.Contains(rp.RoleId) select new { rp.RoleId, p.Name }).ToListAsync(ct);
            return new PagedResult<object>(page.Items.Select(x => (object)new { x.Id, x.Name, x.Description, x.Protected, permissionCount = grants.Count(p => p.RoleId == x.Id), permissions = grants.Where(p => p.RoleId == x.Id).Select(p => p.Name).Order().ToArray() }).ToArray(), page.Page, page.PageSize, page.TotalCount);
        });
        api.MapPost("/roles", async (RoleInput input, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("roles.create", ct); actor.RequireGlobal();
            var entity = new Role(); SetRole(entity, input); db.Roles.Add(entity); await db.SaveChangesAsync(ct); return Results.Created($"/api/v1/roles/{entity.Id}", entity);
        });
        api.MapPut("/roles/{id:guid}", async (Guid id, RoleInput input, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("roles.update", ct); actor.RequireGlobal();
            var entity = await db.Roles.FindAsync([id], ct) ?? throw new MissingEntityException("Rol no encontrado.");
            GuardRole(entity); SetRole(entity, input); await db.SaveChangesAsync(ct); return Results.Ok(entity);
        });
        api.MapDelete("/roles/{id:guid}", async (Guid id, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("roles.delete", ct); actor.RequireGlobal();
            var entity = await db.Roles.FindAsync([id], ct) ?? throw new MissingEntityException("Rol no encontrado."); GuardRole(entity);
            if (await db.UserRoles.AnyAsync(x => x.RoleId == id, ct)) throw new RequestInputException("Retira las asignaciones del rol antes de eliminarlo.");
            db.Roles.Remove(entity); await db.SaveChangesAsync(ct); return Results.NoContent();
        });
        api.MapPut("/roles/{id:guid}/permissions", async (Guid id, RolePermissionsInput input, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("roles.assign_permissions", ct); actor.RequireGlobal();
            var role = await db.Roles.FindAsync([id], ct) ?? throw new MissingEntityException("Rol no encontrado."); GuardRole(role);
            if (input.Permissions is null || input.Permissions.Length > 300) throw new RequestInputException("Lista de permisos inválida.");
            var names = input.Permissions.Distinct(StringComparer.Ordinal).ToArray();
            var grants = await db.Permissions.Where(x => names.Contains(x.Name)).ToListAsync(ct);
            if (grants.Count != names.Length) throw new RequestInputException("Uno o más permisos no existen.");
            var existing = await db.RolePermissions.Where(x => x.RoleId == id).ToListAsync(ct);
            var wanted = grants.Select(x => x.Id).ToHashSet();
            db.RolePermissions.RemoveRange(existing.Where(x => !wanted.Contains(x.PermissionId)));
            var current = existing.Select(x => x.PermissionId).ToHashSet();
            db.RolePermissions.AddRange(wanted.Where(x => !current.Contains(x)).Select(x => new RolePermission { RoleId = id, PermissionId = x }));
            await db.SaveChangesAsync(ct); return Results.Ok(new { role.Id, permissions = names, permissionCount = names.Length });
        });
        api.MapGet("/roles/{id:guid}/users", async (Guid id, HttpRequest request, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("users.read", ct);
            actor.RequireUnrestrictedChannel();
            var query = db.Users.AsNoTracking().Where(u => db.UserRoles.Any(r => r.UserId == u.Id && r.RoleId == id));
            var partner = AdminPage.Partner(request, actor);
            if (partner.HasValue) query = query.Where(x => x.PartnerId == partner);
            return await AdminPage.Read(query.OrderBy(x => x.DisplayName), request, ct);
        });

        api.MapGet("/permissions", async (HttpRequest request, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("permissions.read", ct);
            var query = db.Permissions.AsNoTracking();
            var search = AdminPage.Search(request);
            if (search is not null) query = query.Where(x => x.Name.ToLower().Contains(search) || x.Module.ToLower().Contains(search));
            return await AdminPage.Read(query.OrderBy(x => x.Module).ThenBy(x => x.Name), request, ct);
        });
        api.MapPost("/permissions", async (PermissionInput input, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("permissions.create", ct); actor.RequireGlobal();
            var name = SafeInput.Code(input.Name, "name");
            if (!name.Contains('.')) throw new RequestInputException("El permiso debe seguir módulo.acción.");
            var entity = new Permission { Name = name, Module = name.Split('.')[0] }; db.Permissions.Add(entity); await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/permissions/{entity.Id}", entity);
        });

        api.MapGet("/webhooks", async (HttpRequest request, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("webhooks.read", ct);
            var query = db.WebhookSubscriptions.AsNoTracking();
            var partner = AdminPage.Partner(request, actor);
            if (partner.HasValue) query = query.Where(x => x.PartnerId == partner);
            return await AdminPage.Read(query.OrderBy(x => x.Url).ThenBy(x => x.Id).Select(x => new { x.Id, x.PartnerId, x.Url, x.EventType, x.Enabled }), request, ct);
        });
        api.MapPost("/webhooks", async (WebhookInput input, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("webhooks.manage", ct); await actor.EnsurePartnerAccess(input.PartnerId, cancellationToken: ct);
            var entity = new WebhookSubscription { PartnerId = input.PartnerId, SigningSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() };
            SetWebhook(entity, input); db.WebhookSubscriptions.Add(entity); await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/webhooks/{entity.Id}", new { entity.Id, entity.PartnerId, entity.Url, entity.EventType, entity.Enabled, signingSecret = entity.SigningSecret });
        });
        api.MapPut("/webhooks/{id:guid}", async (Guid id, WebhookInput input, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("webhooks.manage", ct);
            var entity = await db.WebhookSubscriptions.FindAsync([id], ct) ?? throw new MissingEntityException("Suscripción no encontrada.");
            await actor.EnsurePartnerAccess(entity.PartnerId, cancellationToken: ct);
            if (entity.PartnerId != input.PartnerId) throw new RequestInputException("No puedes transferir la suscripción.");
            SetWebhook(entity, input); await db.SaveChangesAsync(ct); return Results.Ok(new { entity.Id, entity.PartnerId, entity.Url, entity.EventType, entity.Enabled });
        });
        api.MapDelete("/webhooks/{id:guid}", async (Guid id, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("webhooks.manage", ct);
            var entity = await db.WebhookSubscriptions.FindAsync([id], ct) ?? throw new MissingEntityException("Suscripción no encontrada.");
            await actor.EnsurePartnerAccess(entity.PartnerId, cancellationToken: ct); entity.Enabled = false; await db.SaveChangesAsync(ct); return Results.NoContent();
        });
        api.MapGet("/settings", async (IHostEnvironment environment, IPaymentGateway payments, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("settings.read", ct); actor.RequireGlobal();
            return Results.Ok(new { platform = "VortexBird Integration Platform", environment = environment.EnvironmentName, externalSystems = "simulated", paymentProvider = payments.Provider, currency = "COP", apiVersion = "v1", maximumPageSize = 100, idempotencyRetentionHours = 24, configurationMode = "environment", mutableThroughPortal = false });
        });
        return api;
    }

    private static void SetPartner(Partner entity, PartnerInput input)
    {
        if (input.RequestsPerMinute is < 10 or > 100000) throw new RequestInputException("requestsPerMinute debe estar entre 10 y 100000.");
        entity.Name = SafeInput.Text(input.Name, "name"); entity.Code = SafeInput.Code(input.Code, "code"); entity.Enabled = input.Enabled; entity.RequestsPerMinute = input.RequestsPerMinute;
    }
    private static void SetChannel(Channel entity, ChannelInput input)
    {
        if (input.RequestsPerMinute is < 10 or > 100000) throw new RequestInputException("requestsPerMinute debe estar entre 10 y 100000.");
        entity.Name = SafeInput.Text(input.Name, "name"); entity.Code = SafeInput.Code(input.Code, "code"); entity.Enabled = input.Enabled; entity.RequestsPerMinute = input.RequestsPerMinute;
    }
    private static void SetProduct(Product entity, ProductInput input)
    { entity.Sku = SafeInput.Code(input.Sku, "sku"); entity.Name = SafeInput.Text(input.Name, "name"); entity.Category = SafeInput.Text(input.Category, "category", 80); entity.Active = input.Active; }
    private static void SetRole(Role entity, RoleInput input)
    {
        var name = SafeInput.Code(input.Name, "name");
        if (name.Equals("SUPER_ADMIN", StringComparison.OrdinalIgnoreCase)) throw new RequestInputException("Nombre de rol reservado.");
        entity.Name = name; entity.Description = SafeInput.Text(input.Description, "description", 500);
    }
    private static void GuardRole(Role role)
    { if (role.Protected || role.Name == "SUPER_ADMIN") throw new AccessDeniedException("El rol protegido requiere un procedimiento administrativo fuera del portal."); }
    private static async Task GuardProtectedUser(Guid userId, IntegrationDbContext db, CancellationToken ct)
    {
        if (await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == userId && (r.Protected || r.Name == "SUPER_ADMIN") select ur).AnyAsync(ct))
            throw new AccessDeniedException("No se permite modificar la cuenta con rol protegido desde el portal.");
    }
    private static async Task SetUser(User entity, UserInput input, IntegrationDbContext db, CancellationToken ct)
    {
        var email = SafeInput.Text(input.Email, "email", 254).ToLowerInvariant();
        if (!MailAddress.TryCreate(email, out var address) || address.Address != email) throw new RequestInputException("Correo electrónico inválido.");
        if (input.PartnerId.HasValue && !await db.Partners.AnyAsync(x => x.Id == input.PartnerId, ct)) throw new RequestInputException("Partner no encontrado.");
        entity.Subject = SafeInput.Text(input.Subject, "subject", 200); entity.Email = email; entity.DisplayName = SafeInput.Text(input.DisplayName, "displayName"); entity.PartnerId = input.PartnerId; entity.Enabled = input.Enabled;
    }
    private static async Task AssignUserRoles(User user, Guid[] roleIds, IntegrationDbContext db, CancellationToken ct)
    {
        if (roleIds is null || roleIds.Length > 50) throw new RequestInputException("Lista de roles inválida.");
        var ids = roleIds.Distinct().ToArray();
        var roles = await db.Roles.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        if (roles.Count != ids.Length) throw new RequestInputException("Uno o más roles no existen.");
        foreach (var role in roles) GuardRole(role);
        var existing = await db.UserRoles.Where(x => x.UserId == user.Id).ToListAsync(ct);
        var previous = existing.Select(x => x.RoleId).ToHashSet();
        db.UserRoles.RemoveRange(existing.Where(x => !ids.Contains(x.RoleId)));
        db.UserRoles.AddRange(ids.Where(x => !previous.Contains(x)).Select(id => new UserRole { UserId = user.Id, RoleId = id }));
    }
    private static void SetWebhook(WebhookSubscription entity, WebhookInput input)
    {
        var url = SafeInput.Text(input.Url, "url", 2048);
        try { entity.Url = WebhookSafety.Validate(url).AbsoluteUri; } catch (ArgumentException error) { throw new RequestInputException(error.Message); }
        string[] events = ["order.confirmed", "payment.approved", "payment.rejected", "shipment.started", "delivery.confirmed"];
        if (!events.Contains(input.EventType)) throw new RequestInputException("Tipo de evento no soportado.");
        entity.EventType = input.EventType; entity.Enabled = input.Enabled;
    }
}

internal static class AdminPage
{
    public static async Task<PagedResult<T>> Read<T>(IQueryable<T> query, HttpRequest request, CancellationToken ct)
    {
        var page = Integer(request, "page", 1, 1, 1000000);
        var size = Integer(request, "pageSize", 10, 1, 100);
        var count = await query.CountAsync(ct);
        return new PagedResult<T>(await query.Skip((page - 1) * size).Take(size).ToListAsync(ct), page, size, count);
    }
    public static int Integer(HttpRequest request, string name, int fallback, int min, int max)
    {
        if (!request.Query.TryGetValue(name, out var raw)) return fallback;
        if (!int.TryParse(raw, out var value) || value < min || value > max) throw new RequestInputException($"{name} debe estar entre {min} y {max}.");
        return value;
    }
    public static string? Search(HttpRequest request)
    {
        var raw = request.Query["search"].ToString().Trim();
        if (raw.Length > 150) throw new RequestInputException("Búsqueda demasiado larga.");
        return raw.Length == 0 ? null : raw.ToLowerInvariant();
    }
    public static Guid? GuidFilter(HttpRequest request, string name)
    {
        var raw = request.Query[name].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!Guid.TryParse(raw, out var id)) throw new RequestInputException($"{name} debe ser UUID.");
        return id;
    }
    public static Guid? Partner(HttpRequest request, HttpActor actor)
    {
        var raw = request.HttpContext.User.FindFirstValue("partner_id");
        if (raw is not null)
        {
            if (!Guid.TryParse(raw, out var claimed)) throw new AccessDeniedException("Claim partner_id inválido.");
            if (actor.PartnerId.HasValue && actor.PartnerId != claimed) throw new AccessDeniedException("El token y la cuenta no pertenecen al mismo partner.");
            return claimed;
        }
        return actor.PartnerId;
    }
    public static Guid[] ClaimedChannels(HttpRequest request)
    {
        var values = request.HttpContext.User.FindAll("channel_id").Select(x => x.Value).ToArray();
        if (values.Any(x => !Guid.TryParse(x, out _))) throw new AccessDeniedException("Claim channel_id inválido.");
        return values.Select(Guid.Parse).Distinct().ToArray();
    }
}

public record PartnerInput(string Name, string Code, bool Enabled = true, int RequestsPerMinute = 120);
public record ChannelInput(Guid PartnerId, string Name, string Code, bool Enabled = true, int RequestsPerMinute = 120);
public record ProductInput(string Sku, string Name, string Category, bool Active = true);
public record UserInput(string Subject, string Email, string DisplayName, Guid? PartnerId = null, bool Enabled = true, Guid[]? RoleIds = null);
public record UserRolesInput(Guid[] RoleIds);
public record RoleInput(string Name, string Description);
public record RolePermissionsInput(string[] Permissions);
public record PermissionInput(string Name);
public record WebhookInput(Guid PartnerId, string Url, string EventType, bool Enabled = true);
