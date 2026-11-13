using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VortexBird.Application.Abstractions;
using VortexBird.Application.Contracts;
using VortexBird.Application.Validation;
using VortexBird.Domain;
using VortexBird.Domain.Entities;

namespace VortexBird.Infrastructure;

public sealed class GetOrderHandler(IntegrationDbContext db, ICurrentActor actor) : IQueryHandler<GetOrderQuery, Order?>
{
    public async Task<Order?> Handle(GetOrderQuery request, CancellationToken ct = default)
    {
        var order = await db.Orders.AsNoTracking().Include(x => x.Items).Include(x => x.Timeline).SingleOrDefaultAsync(x => x.Id == request.Id, ct);
        if (order is not null) await actor.EnsurePartnerAccess(order.PartnerId, order.ChannelId, ct);
        return order;
    }
}

public static class RequestFingerprint
{
    public static string For(CreateOrderRequest request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        request.PartnerId, request.ChannelId, request.StoreId, request.CustomerId,
        Items = request.Items.OrderBy(x => x.Sku, StringComparer.Ordinal).Select(x => new { x.Sku, x.Quantity }),
        request.PaymentOutcome
    }))));
}

public sealed class CreateOrderHandler(IntegrationDbContext db, ICurrentActor actor, IPricingAdapter pricing, IInventoryAdapter inventory, ICustomerAdapter customers) : ICommandHandler<CreateOrderCommand, OrderReceipt>
{
    public async Task<OrderReceipt> Handle(CreateOrderCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        CreateOrderRequestValidator.ValidateAndThrow(request);
        if (string.IsNullOrWhiteSpace(command.Key) || command.Key.Length > 128) throw new DomainException("invalid_idempotency_key", "Idempotency-Key es obligatorio y debe tener hasta 128 caracteres.");
        await actor.EnsurePartnerAccess(request.PartnerId, request.ChannelId, ct);
        var hash = RequestFingerprint.For(request);
        // A database advisory transaction lock serializes equal tenant/key attempts across replicas.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({request.PartnerId + ":" + command.Key}, 0))", ct);
        var existing = await db.IdempotencyRecords.SingleOrDefaultAsync(x => x.PartnerId == request.PartnerId && x.Key == command.Key, ct);
        if (existing is not null)
        {
            // Never reuse an expired key for a second order; require a fresh key after the retention window.
            if (existing.RequestHash != hash) throw new DomainException("idempotency_conflict", "La clave ya fue usada con un contenido diferente.");
            if (existing.ExpiresAt < DateTimeOffset.UtcNow) throw new DomainException("idempotency_expired", "La clave expiró. Usa una clave nueva.");
            if (existing.State != "completed") throw new DomainException("idempotency_processing", "La solicitud está siendo procesada.");
            return JsonSerializer.Deserialize<OrderReceipt>(existing.ResponseJson)! with { Replayed = true };
        }
        var partner = await db.Partners.SingleOrDefaultAsync(x => x.Id == request.PartnerId && x.Enabled, ct) ?? throw new DomainException("partner_disabled", "Partner no disponible.");
        _ = partner;
        if (!await db.Channels.AnyAsync(x => x.Id == request.ChannelId && x.PartnerId == request.PartnerId && x.Enabled, ct)) throw new DomainException("invalid_channel", "Canal inválido.");
        var order = new Order { PartnerId = request.PartnerId, ChannelId = request.ChannelId, StoreId = request.StoreId, CustomerId = request.CustomerId, CorrelationId = actor.CorrelationId };
        await customers.ValidateCustomer(request.CustomerId, request.PartnerId, ct);
        db.Orders.Add(order);
        foreach (var line in request.Items.OrderBy(x => x.Sku, StringComparer.Ordinal))
        {
            if (!await db.Products.AnyAsync(x => x.Sku == line.Sku && x.Active, ct)) throw new DomainException("unknown_product", $"SKU no disponible: {line.Sku}");
            var price = await pricing.GetPrice(line.Sku, request.PartnerId, request.ChannelId, request.CustomerId, ct);
            if (price.Currency != order.Currency || price.Amount <= 0) throw new DomainException("invalid_price", "Moneda o precio inválidos.");
            await inventory.Reserve(order.Id, line.Sku, request.StoreId, line.Quantity, ct);
            order.Items.Add(new OrderItem { OrderId = order.Id, Sku = line.Sku, Quantity = line.Quantity, UnitPrice = price.Amount });
            order.Total += price.Amount * line.Quantity;
        }
        order.TransitionTo(OrderStates.Reserved, "Inventario reservado; procesamiento asíncrono de pago en cola.", DateTimeOffset.UtcNow);
        var receipt = new OrderReceipt(order.Id, order.Status, order.PaymentStatus, order.Total, order.Currency, order.CorrelationId);
        db.IdempotencyRecords.Add(new IdempotencyRecord { PartnerId = request.PartnerId, Key = command.Key, RequestHash = hash, State = "completed", OrderId = order.Id, ResponseJson = JsonSerializer.Serialize(receipt), ExpiresAt = DateTimeOffset.UtcNow.AddHours(24) });
        db.OutboxMessages.Add(new OutboxMessage { PartnerId = order.PartnerId, Type = "order.created", CorrelationId = order.CorrelationId, Payload = JsonSerializer.Serialize(new { orderId = order.Id, paymentOutcome = request.PaymentOutcome }) });
        db.ActivityLogs.Add(new ActivityLog { UserId = actor.Subject, PartnerId = order.PartnerId, ChannelId = order.ChannelId, Action = "create", Module = "orders", EntityId = order.Id.ToString(), CorrelationId = order.CorrelationId, Description = "Orden y reserva creadas de forma atómica; pago simulado pendiente." });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return receipt;
    }
}

public sealed class OrderSaga(IntegrationDbContext db, IPaymentGateway gateway, IInventoryAdapter inventory)
{
    public async Task Advance(Guid orderId, string outcome, CancellationToken ct)
    {
        var snapshot = await db.Orders.AsNoTracking().SingleAsync(x => x.Id == orderId, ct);
        if (snapshot.Status != OrderStates.Reserved) return;
        // Provider call is outside SQL transaction; stable operation key enables safe reconciliation/replay.
        var result = await gateway.Initiate(new PaymentRequest(snapshot.Id, snapshot.PartnerId, snapshot.Total, snapshot.Currency, $"payment:{snapshot.Id}", outcome.ToLowerInvariant()), ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({orderId.ToString()}, 0))", ct);
        var order = await db.Orders.Include(x => x.Timeline).SingleAsync(x => x.Id == orderId, ct);
        if (order.Status != OrderStates.Reserved) return;
        outcome = result.Status;
        order.TransitionTo(OrderStates.AwaitingPayment, "Intención de pago creada en simulador explícito.", DateTimeOffset.UtcNow);
        var payment = new Payment { OrderId = order.Id, PartnerId = order.PartnerId, Reference = result.Reference, Provider = result.Provider, Amount = order.Total, Status = outcome };
        db.Payments.Add(payment);
        if (outcome is "approved" or "rejected") await ApplyOutcome(order, payment, outcome, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task Resolve(Guid orderId, string outcome, CancellationToken ct)
    {
        if (outcome is not ("approved" or "rejected")) throw new DomainException("invalid_outcome", "Resultado inválido.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({orderId.ToString()}, 0))", ct);
        var order = await db.Orders.Include(x => x.Timeline).SingleAsync(x => x.Id == orderId, ct);
        if (order.Status != OrderStates.AwaitingPayment) throw new DomainException("invalid_order_state", "La orden no espera pago.");
        var payment = await db.Payments.SingleAsync(x => x.OrderId == order.Id, ct);
        if (payment.Provider != "simulator") throw new DomainException("not_simulated", "Solo pagos simulados.");
        await ApplyOutcome(order, payment, outcome, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task ApplyOutcome(Order order, Payment payment, string outcome, CancellationToken ct)
    {
        var reservations = await db.InventoryReservations.Where(x => x.OrderId == order.Id && x.Status == "active").ToListAsync(ct);
        foreach (var reservation in reservations)
        {
            if (outcome == "approved")
            {
                var affected = await db.Stocks.Where(x => x.Id == reservation.StockId && x.Reserved >= reservation.Quantity).ExecuteUpdateAsync(s => s.SetProperty(x => x.OnHand, x => x.OnHand - reservation.Quantity).SetProperty(x => x.Reserved, x => x.Reserved - reservation.Quantity).SetProperty(x => x.Version, x => x.Version + 1), ct);
                if (affected != 1) throw new DomainException("reservation_conflict", "Reserva inconsistente; requiere revisión.");
            }
            else await inventory.Release(reservation.Id.ToString(), ct);
            reservation.Status = outcome == "approved" ? "committed" : "released";
        }
        payment.Status = outcome;
        order.PaymentStatus = outcome;
        if (outcome == "approved") order.TransitionTo(OrderStates.Confirmed, "Pago simulado aprobado e inventario consumido.", DateTimeOffset.UtcNow);
        else { order.TransitionTo(OrderStates.Compensating, "Pago rechazado; liberando reserva.", DateTimeOffset.UtcNow); order.TransitionTo(OrderStates.Cancelled, "Reserva liberada.", DateTimeOffset.UtcNow); }
        db.OutboxMessages.Add(new OutboxMessage { PartnerId = order.PartnerId, Type = $"payment.{outcome}", CorrelationId = order.CorrelationId, Payload = JsonSerializer.Serialize(new { orderId = order.Id, reference = payment.Reference, payment.Status }) });
        if (outcome == "approved") db.OutboxMessages.Add(new OutboxMessage { PartnerId = order.PartnerId, Type = "order.confirmed", CorrelationId = order.CorrelationId, Payload = JsonSerializer.Serialize(new { orderId = order.Id, order.Status }) });
    }

    public async Task Cancel(Guid orderId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({orderId.ToString()}, 0))", ct);
        var order = await db.Orders.Include(x => x.Timeline).SingleAsync(x => x.Id == orderId, ct);
        if (order.Status == OrderStates.Cancelled) return;
        if (order.Status is not (OrderStates.Reserved or OrderStates.AwaitingPayment)) throw new DomainException("cannot_cancel", "Solo pueden cancelarse órdenes con reserva o pago pendiente.");
        foreach (var r in await db.InventoryReservations.Where(x => x.OrderId == order.Id && x.Status == "active").ToListAsync(ct))
        {
            await inventory.Release(r.Id.ToString(), ct);
        }
        order.TransitionTo(OrderStates.Compensating, "Compensación solicitada.", DateTimeOffset.UtcNow);
        order.TransitionTo(OrderStates.Cancelled, "Reservas liberadas.", DateTimeOffset.UtcNow);
        var payment = await db.Payments.SingleOrDefaultAsync(x => x.OrderId == order.Id, ct);
        if (payment is not null) payment.Status = "cancelled";
        order.PaymentStatus = "cancelled";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
