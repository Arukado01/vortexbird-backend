using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VortexBird.Application.Contracts;
using VortexBird.Application.Abstractions;
using VortexBird.Domain;
using VortexBird.Domain.Entities;
using VortexBird.Infrastructure;

namespace VortexBird.API;

public static class OperationalEndpoints
{
    public static void MapOperationalEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/customers", async (IntegrationDbContext db, HttpActor actor, HttpRequest request, CancellationToken ct) =>
        {
            await actor.Require("orders.read", ct);
            var q = db.Customers.AsNoTracking().AsQueryable();
            if (actor.PartnerId.HasValue) q = q.Where(x => x.PartnerId == actor.PartnerId);
            if (Guid.TryParse(request.Query["partnerId"], out var partner)) q = q.Where(x => x.PartnerId == partner);
            return await Page(q.OrderBy(x => x.ExternalId), request, ct);
        });
        api.MapGet("/integrations/oms/orders", async (IntegrationDbContext db, HttpActor actor, HttpRequest request, CancellationToken ct) =>
        {
            await actor.Require("integrations.read", ct);
            var q = db.OmsReplicas.AsNoTracking().AsQueryable();
            if (actor.PartnerId.HasValue) q = q.Where(x => x.PartnerId == actor.PartnerId);
            if (actor.ChannelIds.Length > 0) q = q.Where(x => actor.ChannelIds.Contains(x.ChannelId));
            return await Page(q.OrderByDescending(x => x.SynchronizedAt), request, ct);
        });
        api.MapGet("/inventory", async (IntegrationDbContext db, HttpActor actor, HttpRequest request, CancellationToken ct) =>
        {
            await actor.Require("inventory.read", ct);
            var query = StockQuery(db, actor, request);
            return await Page(query.OrderBy(x => x.Sku).Select(x => new { x.Id, x.PartnerId, x.ChannelId, x.StoreId, x.Sku, x.OnHand, x.Reserved, Available = x.OnHand - x.Reserved, x.Version }), request, ct);
        });
        api.MapGet("/inventory/stock", async (string sku, string storeId, Guid channelId, Guid? partnerId, IntegrationDbContext db, HttpActor actor, ReadCache cache, IInventoryAdapter adapter, CancellationToken ct) =>
        {
            await actor.Require("inventory.read", ct);
            var partner = partnerId ?? actor.PartnerId ?? await db.Channels.Where(x => x.Id == channelId).Select(x => x.PartnerId).SingleOrDefaultAsync(ct);
            await actor.EnsurePartnerAccess(partner, channelId, ct);
            var key = $"stock:{partner}:{channelId}:{storeId}:{sku}";
            var cached = await cache.Get<StockResult>(key, ct);
            if (cached is not null) return Results.Ok(cached);
            var result = await adapter.GetStock(sku, storeId, partner, channelId, ct);
            await cache.Set(key, result, 10, ct);
            return Results.Ok(result);
        });
        api.MapGet("/pricing/quote", async (string sku, Guid channelId, Guid? partnerId, string? customerId, IntegrationDbContext db, HttpActor actor, ReadCache cache, IPricingAdapter adapter, CancellationToken ct) =>
        {
            await actor.Require("pricing.read", ct);
            var partner = partnerId ?? actor.PartnerId ?? await db.Channels.Where(x => x.Id == channelId).Select(x => x.PartnerId).SingleOrDefaultAsync(ct);
            await actor.EnsurePartnerAccess(partner, channelId, ct);
            var key = $"price:{partner}:{channelId}:{sku}:{customerId}";
            var cached = await cache.Get<PriceResult>(key, ct);
            if (cached is not null) return Results.Ok(cached);
            var price = await adapter.GetPrice(sku, partner, channelId, customerId, ct);
            await cache.Set(key, price, 60, ct); return Results.Ok(price);
        });
        api.MapPut("/inventory/{id:guid}", async (Guid id, StockUpdate input, IntegrationDbContext db, HttpActor actor, ReadCache cache, CancellationToken ct) =>
        {
            await actor.Require("inventory.manage", ct);
            var stock = await db.Stocks.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new MissingEntityException("Inventario no encontrado.");
            await actor.EnsurePartnerAccess(stock.PartnerId, stock.ChannelId, ct);
            if (input.OnHand < stock.Reserved || input.OnHand > 10000000) throw new RequestInputException("El inventario no puede ser menor a las reservas.");
            stock.OnHand = input.OnHand; stock.Version++;
            await db.SaveChangesAsync(ct);
            await cache.Invalidate($"stock:{stock.PartnerId}:{stock.ChannelId}:{stock.StoreId}:{stock.Sku}", ct);
            return Results.Ok(stock);
        });
        api.MapGet("/pricing", async (IntegrationDbContext db, HttpActor actor, HttpRequest request, CancellationToken ct) =>
        {
            await actor.Require("pricing.read", ct);
            var q = db.Prices.AsNoTracking().AsQueryable();
            if (actor.PartnerId.HasValue) q = q.Where(x => x.PartnerId == actor.PartnerId);
            if (actor.ChannelIds.Length > 0) q = q.Where(x => actor.ChannelIds.Contains(x.ChannelId));
            if (Guid.TryParse(request.Query["partnerId"], out var partner)) { await actor.EnsurePartnerAccess(partner, cancellationToken: ct); q = q.Where(x => x.PartnerId == partner); }
            if (Guid.TryParse(request.Query["channelId"], out var channel)) q = q.Where(x => x.ChannelId == channel);
            if (!string.IsNullOrWhiteSpace(request.Query["sku"])) { var sku = request.Query["sku"].ToString(); q = q.Where(x => x.Sku == sku); }
            if (!string.IsNullOrWhiteSpace(request.Query["customerId"])) { var customer = request.Query["customerId"].ToString(); q = q.Where(x => x.CustomerId == customer || x.CustomerId == null); }
            var search = request.Query["search"].ToString(); if (search.Length > 0) q = q.Where(x => x.Sku.Contains(search));
            return await Page(q.OrderBy(x => x.Sku), request, ct);
        });
        api.MapPut("/pricing/{id:guid}", async (Guid id, PriceUpdate input, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("pricing.manage", ct);
            var price = await db.Prices.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new MissingEntityException("Precio no encontrado.");
            await actor.EnsurePartnerAccess(price.PartnerId, price.ChannelId, ct);
            if (input.Amount <= 0 || input.Amount > 1000000000 || input.Currency != "COP") throw new RequestInputException("Precio positivo en COP requerido.");
            price.Amount = input.Amount; price.Currency = input.Currency;
            await db.SaveChangesAsync(ct); return Results.Ok(price);
        });
        api.MapGet("/orders", async (IntegrationDbContext db, HttpActor actor, HttpRequest request, CancellationToken ct) =>
        {
            await actor.Require("orders.read", ct);
            var q = ScopedOrders(db, actor);
            var status = request.Query["status"].ToString(); if (status.Length > 0) q = q.Where(x => x.Status == status);
            if (Guid.TryParse(request.Query["partnerId"], out var partner)) { await actor.EnsurePartnerAccess(partner, cancellationToken: ct); q = q.Where(x => x.PartnerId == partner); }
            if (Guid.TryParse(request.Query["channelId"], out var channel)) q = q.Where(x => x.ChannelId == channel);
            var search = request.Query["search"].ToString(); if (Guid.TryParse(search, out var id)) q = q.Where(x => x.Id == id); else if (search.Length > 0) q = q.Where(x => x.CustomerId.Contains(search));
            return await Page(q.OrderByDescending(x => x.CreatedAt), request, ct);
        });
        api.MapGet("/orders/{id:guid}", async (Guid id, GetOrderHandler handler, HttpActor actor, CancellationToken ct) => { await actor.Require("orders.read", ct); return await handler.Handle(new GetOrderQuery(id), ct) is { } order ? Results.Ok(order) : Results.NotFound(); });
        api.MapPost("/orders", async (CreateOrderRequest input, HttpRequest request, CreateOrderHandler handler, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("orders.create", ct);
            var receipt = await handler.Handle(new CreateOrderCommand(input, request.Headers["Idempotency-Key"].ToString()), ct);
            return Results.Accepted($"/api/v1/orders/{receipt.Id}", receipt);
        });
        api.MapPost("/orders/{id:guid}/cancel", async (Guid id, GetOrderHandler query, OrderSaga saga, HttpActor actor, CancellationToken ct) => { await actor.Require("orders.cancel", ct); _ = await query.Handle(new GetOrderQuery(id), ct) ?? throw new MissingEntityException("Orden no encontrada."); await saga.Cancel(id, ct); return Results.Ok(new { status = "cancelled" }); });
        api.MapPost("/orders/{id:guid}/status", async (Guid id, OrderStatusInput input, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("orders.update", ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await VortexBird.Infrastructure.Payments.PaymentCheckoutService.LockOrder(db, id, ct);
            var order = await db.Orders.Include(x => x.Timeline).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new MissingEntityException("Orden no encontrada.");
            await actor.EnsurePartnerAccess(order.PartnerId, order.ChannelId, ct);
            if (input.Status is not (OrderStates.Shipped or OrderStates.Delivered)) throw new RequestInputException("Solo se admite shipped o delivered.");
            if (input.Status == OrderStates.Shipped && order.PaymentStatus != PaymentStates.Approved)
                throw new DomainException("payment_not_approved", "El despacho requiere un pago aprobado y sin reversión pendiente.");
            db.RecordOrderTransition(order, input.Status, "Actualización de fulfillment simulado.", DateTimeOffset.UtcNow);
            db.OutboxMessages.Add(new OutboxMessage { PartnerId = order.PartnerId, CorrelationId = actor.CorrelationId, Type = input.Status == OrderStates.Shipped ? "shipment.started" : "delivery.confirmed", Payload = JsonSerializer.Serialize(new { orderId = order.Id, status = order.Status }) });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(order);
        });
        api.MapGet("/payments", async (IntegrationDbContext db, HttpActor actor, HttpRequest request, CancellationToken ct) =>
        {
            await actor.Require("payments.read", ct);
            var orderIds = ScopedOrders(db, actor).Select(x => x.Id);
            var q = db.Payments.AsNoTracking().Where(x => orderIds.Contains(x.OrderId));
            var status = request.Query["status"].ToString(); if (status.Length > 0) q = q.Where(x => x.Status == status);
            return await Page(q.OrderByDescending(x => x.CreatedAt), request, ct);
        });
        api.MapPost("/payments/{id:guid}/simulate", async (Guid id, PaymentOutcomeInput input, IntegrationDbContext db, HttpActor actor, OrderSaga saga, CancellationToken ct) =>
        {
            await actor.Require("payments.manage", ct);
            var payment = await db.Payments.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new MissingEntityException("Pago no encontrado.");
            var order = await db.Orders.SingleAsync(x => x.Id == payment.OrderId, ct);
            await actor.EnsurePartnerAccess(order.PartnerId, order.ChannelId, ct);
            await saga.Resolve(payment.OrderId, input.Outcome, ct); return Results.Ok(new { status = input.Outcome });
        });
        api.MapGet("/integrations", async (IntegrationDbContext db, HttpActor actor, HttpRequest request, CancellationToken ct) => { await actor.Require("integrations.read", ct); return await Page(db.Integrations.AsNoTracking().OrderBy(x => x.System), request, ct); });
        api.MapPut("/integrations/{id:guid}", async (Guid id, IntegrationInput input, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("integrations.manage", ct); actor.RequireGlobal();
            if (input.FailureMode is not ("none" or "unavailable" or "timeout")) throw new RequestInputException("Modo de simulación inválido.");
            var integration = await db.Integrations.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new MissingEntityException("Integración no encontrada.");
            integration.FailureMode = input.FailureMode; await db.SaveChangesAsync(ct); return Results.Ok(integration);
        });
        api.MapPost("/integrations/{id:guid}/test", async (Guid id, IntegrationDbContext db, HttpActor actor, AdapterCircuit circuit, CancellationToken ct) =>
        {
            await actor.Require("integrations.manage", ct); actor.RequireGlobal();
            var integration = await db.Integrations.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new MissingEntityException("Integración no encontrada.");
            var timer = Stopwatch.StartNew(); string? error = null;
            try { circuit.Check(integration.System); if (integration.FailureMode == "timeout") { await Task.Delay(300, ct); throw new TimeoutException("Tiempo límite simulado."); } if (integration.FailureMode == "unavailable") throw new HttpRequestException("Indisponibilidad simulada."); circuit.Success(integration.System); integration.Status = "healthy"; }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException) { circuit.Fail(integration.System); integration.Status = "unhealthy"; error = ex.Message; }
            integration.LatencyMs = timer.Elapsed.TotalMilliseconds; integration.LastCheckedAt = DateTimeOffset.UtcNow;
            db.IntegrationExecutions.Add(new IntegrationExecution { IntegrationId = id, Operation = "health-check", Status = integration.Status, DurationMs = timer.Elapsed.TotalMilliseconds, Error = error, CorrelationId = actor.CorrelationId });
            await db.SaveChangesAsync(ct); return Results.Ok(new { integration.Id, integration.Status, integration.LatencyMs, mode = "simulated", error });
        });
        api.MapGet("/integration-executions", async (IntegrationDbContext db, HttpActor actor, HttpRequest request, CancellationToken ct) =>
        {
            await actor.Require("integrations.read", ct);
            actor.RequireUnrestrictedChannel();
            var q = db.IntegrationExecutions.AsNoTracking().AsQueryable();
            if (actor.PartnerId.HasValue) q = q.Where(x => x.PartnerId == actor.PartnerId);
            var filter = request.Query["integrationId"].ToString();
            if (filter.Length > 0)
            {
                if (!Guid.TryParse(filter, out var integrationId)) throw new RequestInputException("integrationId debe ser un UUID.");
                q = q.Where(x => x.IntegrationId == integrationId);
            }
            return await Page(q.OrderByDescending(x => x.CreatedAt), request, ct);
        });
        api.MapGet("/events", async (IntegrationDbContext db, HttpActor actor, HttpRequest request, CancellationToken ct) =>
        {
            await actor.Require("events.read", ct);
            actor.RequireUnrestrictedChannel();
            var q = db.OutboxMessages.AsNoTracking().AsQueryable(); if (actor.PartnerId.HasValue) q = q.Where(x => x.PartnerId == actor.PartnerId);
            var status = request.Query["status"].ToString(); if (status.Length > 0) q = q.Where(x => x.Status == status);
            return await Page(q.OrderByDescending(x => x.CreatedAt), request, ct);
        });
        api.MapPost("/events/{id:guid}/retry", async (Guid id, IntegrationDbContext db, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("events.manage", ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var message = await db.OutboxMessages.FromSqlInterpolated($"SELECT * FROM \"OutboxMessages\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw new MissingEntityException("Evento no encontrado.");
            await actor.EnsurePartnerAccess(message.PartnerId, cancellationToken: ct);
            if (message.Status != "dead_letter") throw new RequestInputException("Solo se reintentan eventos de DLQ.");
            await db.Set<VortexBird.Infrastructure.Messaging.MessagingInbox>()
                .Where(x => x.EventId == id && x.PartnerId == message.PartnerId && x.Status == "dead_letter")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "pending").SetProperty(x => x.Attempts, 0)
                    .SetProperty(x => x.LeaseId, "").SetProperty(x => x.LeaseUntil, DateTimeOffset.UtcNow), ct);
            message.Status = "pending"; message.Attempts = 0; message.NextAttemptAt = DateTimeOffset.UtcNow; message.LastError = null;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(message);
        });
        api.MapGet("/webhook-deliveries", async (IntegrationDbContext db, HttpActor actor, HttpRequest request, CancellationToken ct) =>
        {
            await actor.Require("webhooks.read", ct);
            var q = db.WebhookDeliveries.AsNoTracking().AsQueryable(); if (actor.PartnerId.HasValue) q = q.Where(x => db.WebhookSubscriptions.Any(s => s.Id == x.SubscriptionId && s.PartnerId == actor.PartnerId));
            return await Page(q.OrderByDescending(x => x.CreatedAt), request, ct);
        });
        api.MapGet("/activity-logs", async (IntegrationDbContext db, HttpActor actor, HttpRequest request, CancellationToken ct) =>
        {
            await actor.Require("activity_logs.read", ct);
            actor.RequireUnrestrictedChannel();
            var q = db.ActivityLogs.AsNoTracking().AsQueryable(); if (actor.PartnerId.HasValue) q = q.Where(x => x.PartnerId == actor.PartnerId);
            var module = request.Query["module"].ToString(); if (module.Length > 0) q = q.Where(x => x.Module == module);
            var user = request.Query["userId"].ToString(); if (user.Length > 0) q = q.Where(x => x.UserId == user);
            var action = request.Query["action"].ToString(); if (action.Length > 0) q = q.Where(x => x.Action == action);
            var result = request.Query["result"].ToString(); if (result.Length > 0) q = q.Where(x => x.Result == result);
            var search = request.Query["search"].ToString(); if (search.Length > 0) q = q.Where(x => x.EntityId.Contains(search) || x.CorrelationId.Contains(search));
            if (DateTimeOffset.TryParse(request.Query["from"], out var from)) q = q.Where(x => x.CreatedAt >= from.ToUniversalTime());
            if (DateTimeOffset.TryParse(request.Query["to"], out var to)) q = q.Where(x => x.CreatedAt <= to.ToUniversalTime());
            return await Page(q.OrderByDescending(x => x.CreatedAt), request, ct);
        });
    }
    public static IQueryable<Order> ScopedOrders(IntegrationDbContext db, HttpActor actor)
    {
        var q = db.Orders.AsNoTracking().AsQueryable();
        if (actor.PartnerId.HasValue) q = q.Where(x => x.PartnerId == actor.PartnerId);
        if (actor.ChannelIds.Length > 0) q = q.Where(x => actor.ChannelIds.Contains(x.ChannelId));
        return q;
    }
    private static IQueryable<Stock> StockQuery(IntegrationDbContext db, HttpActor actor, HttpRequest request)
    {
        var q = db.Stocks.AsNoTracking().AsQueryable();
        if (actor.PartnerId.HasValue) q = q.Where(x => x.PartnerId == actor.PartnerId);
        if (actor.ChannelIds.Length > 0) q = q.Where(x => actor.ChannelIds.Contains(x.ChannelId));
        if (Guid.TryParse(request.Query["partnerId"], out var partner)) q = q.Where(x => x.PartnerId == partner);
        if (Guid.TryParse(request.Query["channelId"], out var channel)) q = q.Where(x => x.ChannelId == channel);
        var sku = request.Query["sku"].ToString(); if (sku.Length > 0) q = q.Where(x => x.Sku == sku);
        var store = request.Query["storeId"].ToString(); if (store.Length > 0) q = q.Where(x => x.StoreId == store);
        var search = request.Query["search"].ToString(); if (search.Length > 0) q = q.Where(x => x.Sku.Contains(search) || x.StoreId.Contains(search));
        return q;
    }
    public static async Task<PagedResult<T>> Page<T>(IQueryable<T> query, HttpRequest request, CancellationToken ct)
    {
        var page = int.TryParse(request.Query["page"], out var p) ? Math.Clamp(p, 1, 100000) : 1;
        var size = int.TryParse(request.Query["pageSize"], out var s) ? Math.Clamp(s, 1, 100) : 20;
        return new PagedResult<T>(await query.Skip((page - 1) * size).Take(size).ToListAsync(ct), page, size, await query.CountAsync(ct));
    }
}
public record StockUpdate(int OnHand);
public record PriceUpdate(decimal Amount, string Currency = "COP");
public record IntegrationInput(string FailureMode);
public record PaymentOutcomeInput(string Outcome);
public record OrderStatusInput(string Status);
