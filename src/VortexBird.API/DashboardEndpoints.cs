using Microsoft.EntityFrameworkCore;
using VortexBird.Infrastructure;

namespace VortexBird.API;

public static class DashboardEndpoints
{
    public static RouteGroupBuilder MapDashboardEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", ReadDashboard);
        api.MapGet("/dashboard/metrics", ReadDashboard);
        return api;
    }

    private static async Task<IResult> ReadDashboard(HttpRequest request, IntegrationDbContext db, HttpActor actor, CancellationToken ct)
    {
        await actor.Require("dashboard.read", ct);
        var days = AdminPage.Integer(request, "days", 14, 1, 90);
        var since = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1 - days), TimeSpan.Zero);
        var scopedPartner = AdminPage.Partner(request, actor);
        var partnerId = AdminPage.GuidFilter(request, "partnerId") ?? scopedPartner;
        var channelId = AdminPage.GuidFilter(request, "channelId");
        var claimedChannels = AdminPage.ClaimedChannels(request);
        if (partnerId.HasValue) await actor.EnsurePartnerAccess(partnerId.Value, channelId, ct);
        if (scopedPartner.HasValue && partnerId != scopedPartner) throw new AccessDeniedException("No tienes acceso al partner solicitado.");
        if (channelId.HasValue && claimedChannels.Length > 0 && !claimedChannels.Contains(channelId.Value)) throw new AccessDeniedException("Canal no permitido.");

        var orders = db.Orders.AsNoTracking().Where(x => x.CreatedAt >= since);
        var stocks = db.Stocks.AsNoTracking();
        var audit = db.ActivityLogs.AsNoTracking().Where(x => x.CreatedAt >= since);
        var events = db.OutboxMessages.AsNoTracking();
        var subscriptions = db.WebhookSubscriptions.AsNoTracking();
        if (partnerId.HasValue)
        {
            orders = orders.Where(x => x.PartnerId == partnerId);
            stocks = stocks.Where(x => x.PartnerId == partnerId);
            audit = audit.Where(x => x.PartnerId == partnerId);
            events = events.Where(x => x.PartnerId == partnerId);
            subscriptions = subscriptions.Where(x => x.PartnerId == partnerId);
        }
        if (channelId.HasValue)
        {
            orders = orders.Where(x => x.ChannelId == channelId);
            stocks = stocks.Where(x => x.ChannelId == channelId);
            audit = audit.Where(x => x.ChannelId == channelId);
        }
        if (claimedChannels.Length > 0)
        {
            orders = orders.Where(x => claimedChannels.Contains(x.ChannelId));
            stocks = stocks.Where(x => claimedChannels.Contains(x.ChannelId));
            audit = audit.Where(x => x.ChannelId.HasValue && claimedChannels.Contains(x.ChannelId.Value));
        }
        var payments = db.Payments.AsNoTracking().Where(x => orders.Any(o => o.Id == x.OrderId));
        var reservations = db.InventoryReservations.AsNoTracking().Where(x => x.Status == "active" && stocks.Any(s => s.Id == x.StockId));
        var deliveries = db.WebhookDeliveries.AsNoTracking().Where(x => subscriptions.Any(s => s.Id == x.SubscriptionId));

        // Aggregates execute in PostgreSQL. The only in-memory work formats at most 90 daily rows.
        var totals = await orders.GroupBy(_ => 1).Select(g => new
        {
            totalOrders = g.Count(),
            pendingOrders = g.Count(x => x.Status == "pending" || x.Status == "reserved" || x.Status == "awaiting_payment" || x.Status == "compensating"),
            confirmedOrders = g.Count(x => x.Status == "confirmed" || x.Status == "shipped" || x.Status == "delivered"),
            cancelledOrders = g.Count(x => x.Status == "cancelled" || x.Status == "failed")
        }).SingleOrDefaultAsync(ct);
        var approvedPayments = await payments.CountAsync(x => x.Status == "approved", ct);
        var rejectedPayments = await payments.CountAsync(x => x.Status == "rejected", ct);
        var totalRevenue = await payments.Where(x => x.Status == "approved").SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
        var availableStock = await stocks.SumAsync(x => (long?)(x.OnHand - x.Reserved), ct) ?? 0;
        var activeReservations = await reservations.CountAsync(ct);
        var integrations = await db.Integrations.AsNoTracking().OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, x.System, x.Mode, x.Status, x.LatencyMs, x.LastCheckedAt }).ToListAsync(ct);
        // Events and webhook subscriptions are partner-scoped. Their tables do not
        // contain a channel; do not disclose sibling-channel counts to scoped tokens.
        var channelRestricted = channelId.HasValue || claimedChannels.Length > 0;
        int? pendingEvents = channelRestricted ? null : await events.CountAsync(x => x.Status == "pending" || x.Status == "processing", ct);
        int? failedWebhooks = channelRestricted ? null : await deliveries.CountAsync(x => x.Status == "failed", ct);
        var daily = await orders.GroupBy(x => DateOnly.FromDateTime(x.CreatedAt.UtcDateTime))
            .Select(g => new { Date = g.Key, Count = g.Count(), Total = g.Sum(x => x.Total) })
            .OrderBy(x => x.Date).ToListAsync(ct);
        var ordersByStatus = await orders.GroupBy(x => x.Status).Select(g => new { status = g.Key, count = g.Count() }).OrderBy(x => x.status).ToListAsync(ct);
        var channelVolumes = await orders.GroupBy(x => x.ChannelId).Select(g => new { ChannelId = g.Key, Count = g.Count() }).ToListAsync(ct);
        var channelIds = channelVolumes.Select(x => x.ChannelId).ToArray();
        var channels = await db.Channels.AsNoTracking().Where(x => channelIds.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct);
        var recentOrders = await orders.OrderByDescending(x => x.CreatedAt).Take(6).Select(x => new { x.Id, x.PartnerId, x.ChannelId, x.CustomerId, x.Status, x.PaymentStatus, x.Total, x.Currency, x.CreatedAt, x.CorrelationId }).ToListAsync(ct);
        var recentActivity = await audit.OrderByDescending(x => x.CreatedAt).Take(6).Select(x => new { x.Id, x.UserId, x.Action, x.Module, x.Result, x.Description, x.CreatedAt, x.CorrelationId }).ToListAsync(ct);
        return Results.Ok(new
        {
            period = new { days, from = since, to = DateTimeOffset.UtcNow },
            partnerMetricsAvailable = !channelRestricted,
            metrics = new { totalOrders = totals?.totalOrders ?? 0, pendingOrders = totals?.pendingOrders ?? 0, confirmedOrders = totals?.confirmedOrders ?? 0, cancelledOrders = totals?.cancelledOrders ?? 0, approvedPayments, rejectedPayments, totalRevenue, availableStock, activeReservations, healthyIntegrations = integrations.Count(x => x.Status == "healthy"), pendingEvents, failedWebhooks },
            ordersByDay = daily.Select(x => new { date = x.Date.ToString("yyyy-MM-dd"), count = x.Count, total = x.Total }),
            ordersByStatus,
            ordersByChannel = channelVolumes.Select(x => new { channel = channels.FirstOrDefault(c => c.Id == x.ChannelId)?.Name ?? x.ChannelId.ToString(), channelId = x.ChannelId, count = x.Count }),
            recentOrders, recentActivity, integrations
        });
    }
}
