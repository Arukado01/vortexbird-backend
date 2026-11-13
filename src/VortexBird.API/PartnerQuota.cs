using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VortexBird.Application.Abstractions;
using VortexBird.Infrastructure;

namespace VortexBird.API;

public sealed record QuotaRequestTarget(Guid PartnerId, Guid ChannelId);

/// <summary>Builds quotas from the authenticated scope and the resource actually requested.</summary>
public sealed class PartnerQuota(IntegrationDbContext db, IQuotaStore store)
{
    public async Task<QuotaDecision> TryAcquire(HttpContext context, HttpActor actor, CancellationToken ct)
    {
        var request = context.Request;
        var path = (request.Path.Value ?? "").TrimEnd('/').ToLowerInvariant();
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var resourceKind = parts.ElementAtOrDefault(2);
        var resourceText = parts.ElementAtOrDefault(3);
        if (resourceKind == "payments" && resourceText == "orders")
        {
            resourceKind = "orders";
            resourceText = parts.ElementAtOrDefault(4);
        }
        var queryChannel = OptionalGuid(request.Query["channelId"], "channelId");
        var queryPartner = OptionalGuid(request.Query["partnerId"], "partnerId");
        QuotaRequestTarget? target = null;

        if (path == "/api/v1/orders" && HttpMethods.IsPost(request.Method))
            target = await ReadOrderTarget(request, ct);
        else if (Guid.TryParse(resourceText, out var resourceId))
        {
            var partner = actor.PartnerId;
            var channels = actor.ChannelIds;
            var orders = db.Orders.AsNoTracking().Where(x => (!partner.HasValue || x.PartnerId == partner) && (channels.Length == 0 || channels.Contains(x.ChannelId)));
            target = resourceKind switch
            {
                "orders" => await orders.Where(x => x.Id == resourceId).Select(x => new QuotaRequestTarget(x.PartnerId, x.ChannelId)).SingleOrDefaultAsync(ct),
                "payments" => await (from payment in db.Payments.AsNoTracking() join order in orders on payment.OrderId equals order.Id where payment.Id == resourceId && payment.PartnerId == order.PartnerId select new QuotaRequestTarget(order.PartnerId, order.ChannelId)).SingleOrDefaultAsync(ct),
                "inventory" => await db.Stocks.AsNoTracking().Where(x => x.Id == resourceId && (!partner.HasValue || x.PartnerId == partner) && (channels.Length == 0 || channels.Contains(x.ChannelId))).Select(x => new QuotaRequestTarget(x.PartnerId, x.ChannelId)).SingleOrDefaultAsync(ct),
                "pricing" => await db.Prices.AsNoTracking().Where(x => x.Id == resourceId && (!partner.HasValue || x.PartnerId == partner) && (channels.Length == 0 || channels.Contains(x.ChannelId))).Select(x => new QuotaRequestTarget(x.PartnerId, x.ChannelId)).SingleOrDefaultAsync(ct),
                "channels" => await db.Channels.AsNoTracking().Where(x => x.Id == resourceId && (!partner.HasValue || x.PartnerId == partner) && (channels.Length == 0 || channels.Contains(x.Id))).Select(x => new QuotaRequestTarget(x.PartnerId, x.Id)).SingleOrDefaultAsync(ct),
                _ => null
            };
            if (target is null && resourceKind is "orders" or "payments" or "inventory" or "pricing" or "channels")
                throw new MissingEntityException("Recurso no encontrado dentro del contexto autorizado.");
        }

        // These handlers actually apply channelId. Other aggregate endpoints must
        // never let an ignored query/header reduce the budgets they consume.
        var filtersChannel = HttpMethods.IsGet(request.Method) && path is "/api/v1/inventory" or "/api/v1/inventory/stock" or "/api/v1/pricing" or "/api/v1/pricing/quote" or "/api/v1/orders";
        if (queryChannel.HasValue && target is null && filtersChannel)
        {
            var channel = await db.Channels.AsNoTracking().Where(x => x.Id == queryChannel && x.Enabled).Select(x => new QuotaRequestTarget(x.PartnerId, x.Id)).SingleOrDefaultAsync(ct)
                ?? throw new AccessDeniedException("Canal desactivado o no permitido.");
            target = channel;
        }
        if (queryChannel.HasValue && (target is null || target.ChannelId != queryChannel))
            throw new RequestInputException("channelId no corresponde al recurso o no es un filtro admitido.");
        var headerChannel = OptionalGuid(request.Headers["X-Channel-Id"], "X-Channel-Id");
        if (headerChannel.HasValue && (target is null || target.ChannelId != headerChannel))
            throw new RequestInputException("X-Channel-Id solo puede confirmar el canal explícito del recurso.");

        var partnerId = target?.PartnerId ?? actor.PartnerId ?? queryPartner;
        if (!partnerId.HasValue) return new QuotaDecision(true, 0); // Global administration remains covered by the outer IP/identity ceiling.
        if (queryPartner.HasValue && queryPartner != partnerId)
            throw new AccessDeniedException("El partner solicitado no coincide con el recurso.");
        await actor.EnsurePartnerAccess(partnerId.Value, target?.ChannelId, ct);
        var partnerLimit = await db.Partners.AsNoTracking().Where(x => x.Id == partnerId && x.Enabled).Select(x => x.RequestsPerMinute).SingleAsync(ct);
        var allowed = actor.ChannelIds;
        var visible = path is "/api/v1/auth/me" or "/api/v1/auth/config" ? [] : await db.Channels.AsNoTracking()
            .Where(x => x.PartnerId == partnerId && x.Enabled && (allowed.Length == 0 || allowed.Contains(x.Id)))
            .Select(x => new ChannelQuota(x.Id, x.RequestsPerMinute)).ToListAsync(ct);
        return await store.TryAcquire(partnerId.Value, QuotaPlanner.Plan(partnerId.Value, partnerLimit, visible, target?.ChannelId), ct);
    }

    public static Guid? OptionalGuid(Microsoft.Extensions.Primitives.StringValues values, string field)
    {
        if (values.Count == 0) return null;
        if (values.Count != 1 || !Guid.TryParse(values[0], out var value) || value == Guid.Empty)
            throw new RequestInputException($"{field} requiere un único UUID válido.");
        return value;
    }

    public static async Task<QuotaRequestTarget> ReadOrderTarget(HttpRequest request, CancellationToken ct)
    {
        const int maximum = 65536;
        if (request.ContentLength > maximum) throw new RequestInputException("El cuerpo de la orden supera 64 KiB.");
        request.EnableBuffering(bufferThreshold: maximum + 1, bufferLimit: maximum + 1);
        using var buffer = new MemoryStream();
        try
        {
            var bytes = new byte[8192];
            int read;
            while ((read = await request.Body.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, maximum + 1 - (int)buffer.Length)), ct)) > 0)
            {
                buffer.Write(bytes, 0, read);
                if (buffer.Length > maximum) throw new RequestInputException("El cuerpo de la orden supera 64 KiB.");
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new RequestInputException("Se requiere una orden JSON.");
            Guid Required(string name)
            {
                var fields = json.RootElement.EnumerateObject().Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (fields.Length != 1 || fields[0].Value.ValueKind != JsonValueKind.String || !fields[0].Value.TryGetGuid(out var value) || value == Guid.Empty)
                    throw new RequestInputException($"{name} requiere un único UUID válido.");
                return value;
            }
            return new QuotaRequestTarget(Required("partnerId"), Required("channelId"));
        }
        catch (JsonException) { throw new RequestInputException("El cuerpo de la orden no contiene JSON válido."); }
        finally { request.Body.Position = 0; }
    }
}
