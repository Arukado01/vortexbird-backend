using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using VortexBird.Application.Abstractions;
using VortexBird.Domain.Entities;
using VortexBird.Infrastructure;

namespace VortexBird.API;

public sealed class HttpActor(IHttpContextAccessor accessor, IntegrationDbContext db) : ICurrentActor
{
    private User? user;
    private HashSet<string>? permissions;
    private AuthorizationScope? scope;
    private HttpContext Context => accessor.HttpContext!;
    public string Subject => Context.User.FindFirstValue("sub") ?? "anonymous";
    public Guid? PartnerId => scope?.PartnerId;
    public Guid[] ChannelIds => scope?.ChannelIds.ToArray() ?? [];
    public string CorrelationId => Context.TraceIdentifier;
    public User? User => user;
    public int RequestsPerMinute { get; private set; } = 300;
    public async Task Initialize(CancellationToken ct)
    {
        user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Subject == Subject && x.Enabled, ct);
        if (user is null) throw new AccessDeniedException("La cuenta no está habilitada en la plataforma.");
        if (Context.User.FindAll("partner_id").Skip(1).Any()) throw new AccessDeniedException("Contexto de partner ambiguo.");
        try { scope = AuthorizationScope.Resolve(user.PartnerId, Context.User.FindFirstValue("partner_id"), Context.User.FindAll("channel_id").Select(x => x.Value)); }
        catch (AuthorizationScopeException ex) { throw new AccessDeniedException(ex.Message); }
        if (PartnerId.HasValue)
        {
            var partner = await db.Partners.AsNoTracking().SingleOrDefaultAsync(x => x.Id == PartnerId && x.Enabled, ct) ?? throw new AccessDeniedException("Partner desactivado.");
            RequestsPerMinute = partner.RequestsPerMinute;
        }
        if (ChannelIds.Length > 0 && await db.Channels.CountAsync(x => ChannelIds.Contains(x.Id) && x.PartnerId == PartnerId && x.Enabled, ct) != ChannelIds.Length) throw new AccessDeniedException("Canal desactivado o ajeno al partner.");
        permissions = (await (from ur in db.UserRoles join rp in db.RolePermissions on ur.RoleId equals rp.RoleId join p in db.Permissions on rp.PermissionId equals p.Id where ur.UserId == user.Id select p.Name).Distinct().ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
    }
    public Task<bool> HasPermission(string permission, CancellationToken ct = default)
    {
        var allowed = permissions?.Contains(permission) == true;
        // Machine identities must also request a matching OAuth scope; human sessions use DB RBAC.
        if (Context.User.FindFirstValue("identity_type") != "human")
            allowed &= (Context.User.FindFirstValue("scope") ?? "").Split(' ').Contains(permission);
        return Task.FromResult(allowed);
    }
    public async Task Require(string permission, CancellationToken ct = default)
    {
        if (!await HasPermission(permission, ct)) throw new AccessDeniedException($"Permiso requerido: {permission}.");
    }
    public void RequireGlobal()
    {
        if (user is null || PartnerId.HasValue) throw new AccessDeniedException("Acción exclusiva de administración global.");
    }
    public void RequireUnrestrictedChannel()
    {
        if (ChannelIds.Length > 0) throw new AccessDeniedException("Este recurso agrega datos de todos los canales. Se requiere acceso sin restricción de canal.");
    }
    public async Task EnsurePartnerAccess(Guid partnerId, Guid? channelId = null, CancellationToken cancellationToken = default)
    {
        if (user is null || (user.PartnerId.HasValue && user.PartnerId != partnerId)) throw new AccessDeniedException("No tienes acceso a este partner.");
        var claimedPartner = Context.User.FindFirstValue("partner_id");
        if (claimedPartner is not null && claimedPartner != partnerId.ToString()) throw new AccessDeniedException("El token no pertenece al partner solicitado.");
        if (!await db.Partners.AnyAsync(x => x.Id == partnerId && x.Enabled, cancellationToken)) throw new AccessDeniedException("Partner desactivado.");
        if (channelId.HasValue)
        {
            if (!await db.Channels.AnyAsync(x => x.Id == channelId && x.PartnerId == partnerId && x.Enabled, cancellationToken)) throw new AccessDeniedException("Canal no permitido.");
            var channels = Context.User.FindAll("channel_id").Select(x => x.Value).ToArray();
            if (channels.Length > 0 && !channels.Contains(channelId.Value.ToString())) throw new AccessDeniedException("El token no permite este canal.");
        }
    }
    public string[] Permissions => permissions?.Order().ToArray() ?? [];
}

public sealed class AccessDeniedException(string message) : Exception(message);
public sealed class MissingEntityException(string message) : Exception(message);
public sealed class RequestInputException(string message) : Exception(message);

public static class SafeInput
{
    public static string Text(string? value, string field, int max = 150)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new RequestInputException($"{field} es obligatorio y admite hasta {max} caracteres.");
        return value.Trim();
    }
    public static string Code(string? value, string field)
    {
        var text = Text(value, field, 64);
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, "^[a-zA-Z0-9._-]+$")) throw new RequestInputException($"{field} contiene caracteres no permitidos.");
        return text;
    }
}
