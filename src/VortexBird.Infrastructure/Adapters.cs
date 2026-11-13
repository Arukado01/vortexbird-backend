using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using VortexBird.Application.Abstractions;
using VortexBird.Application.Contracts;
using VortexBird.Domain;
using VortexBird.Domain.Entities;

namespace VortexBird.Infrastructure;

public sealed class SimulatorGuard(IntegrationDbContext db, AdapterCircuit circuit)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Bulkheads = new();
    public async Task<T> Execute<T>(string system, string operation, Func<Task<T>> work, Guid? partner, CancellationToken ct)
    {
        var bulkhead = Bulkheads.GetOrAdd(system, _ => new SemaphoreSlim(16));
        if (!await bulkhead.WaitAsync(0, ct)) throw new HttpRequestException("Capacidad del sistema legado ocupada; reintentar con backoff.");
        var timer = Stopwatch.StartNew();
        try
        {
            circuit.Check(system);
            var integration = await db.Integrations.SingleOrDefaultAsync(x => x.System == system, ct);
            if (integration?.FailureMode == "unavailable") throw new HttpRequestException($"{system}: indisponibilidad simulada.");
            if (integration?.FailureMode == "timeout") { await Task.Delay(250, ct); throw new TimeoutException($"{system}: timeout simulado."); }
            var result = await work();
            circuit.Success(system);
            if (integration is not null) db.IntegrationExecutions.Add(new IntegrationExecution { IntegrationId = integration.Id, PartnerId = partner, Operation = operation, DurationMs = timer.Elapsed.TotalMilliseconds, Status = "success" });
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException) { circuit.Fail(system); throw; }
        finally { bulkhead.Release(); }
    }
}

// External-shaped records deliberately remain behind each ACL. Simulators use persistent local state.
public sealed class ErpPricingAdapter(IntegrationDbContext db, SimulatorGuard guard) : IPricingAdapter
{
    private sealed record ErpPrice(string CodigoArticulo, decimal PrecioNeto, string Moneda, string? CuentaCliente);
    public string System => "ERP";
    public Task<PriceResult> GetPrice(string sku, Guid partnerId, Guid channelId, string? customerId, CancellationToken ct = default) => guard.Execute(System, "get-price", async () =>
    {
        var price = await db.Prices.AsNoTracking().Where(x => x.PartnerId == partnerId && x.ChannelId == channelId && x.Sku == sku && (x.CustomerId == customerId || x.CustomerId == null)).OrderByDescending(x => x.CustomerId != null).FirstOrDefaultAsync(ct) ?? throw new DomainException("missing_price", "No existe precio para " + sku);
        var external = new ErpPrice(price.Sku, price.Amount, price.Currency, price.CustomerId);
        return new PriceResult(external.CodigoArticulo, channelId, external.CuentaCliente, external.PrecioNeto, external.Moneda, DateTimeOffset.UtcNow);
    }, partnerId, ct);
    public Task<AdapterHealth> CheckHealth(CancellationToken ct = default) => guard.Execute(System, "health", () => Task.FromResult(new AdapterHealth(System, true, 0, "simulated", null)), null, ct);
}

public sealed class WmsInventoryAdapter(IntegrationDbContext db, SimulatorGuard guard) : IInventoryAdapter
{
    private sealed record WarehouseStock(string ItemCode, string Warehouse, int PhysicalUnits, int AllocatedUnits);
    public string System => "WMS";
    public Task<StockResult> GetStock(string sku, string storeId, Guid partnerId, Guid channelId, CancellationToken ct = default) => guard.Execute(System, "get-stock", async () =>
    {
        var stock = await db.Stocks.AsNoTracking().SingleOrDefaultAsync(x => x.PartnerId == partnerId && x.ChannelId == channelId && x.StoreId == storeId && x.Sku == sku, ct) ?? throw new DomainException("stock_not_found", "Inventario no encontrado.");
        var external = new WarehouseStock(stock.Sku, stock.StoreId, stock.OnHand, stock.Reserved);
        return new StockResult(external.ItemCode, external.Warehouse, channelId, external.PhysicalUnits, external.AllocatedUnits, external.PhysicalUnits - external.AllocatedUnits, DateTimeOffset.UtcNow);
    }, partnerId, ct);
    public Task<string> Reserve(Guid orderId, string sku, string storeId, int quantity, CancellationToken ct = default) => guard.Execute(System, "reserve-stock", async () =>
    {
        var order = db.Orders.Local.SingleOrDefault(x => x.Id == orderId) ?? await db.Orders.SingleAsync(x => x.Id == orderId, ct);
        var stock = await db.Stocks.AsNoTracking().SingleOrDefaultAsync(x => x.PartnerId == order.PartnerId && x.ChannelId == order.ChannelId && x.StoreId == storeId && x.Sku == sku, ct) ?? throw new DomainException("insufficient_stock", "Inventario no disponible.");
        var affected = await db.Stocks.Where(x => x.Id == stock.Id && x.OnHand - x.Reserved >= quantity).ExecuteUpdateAsync(s => s.SetProperty(x => x.Reserved, x => x.Reserved + quantity).SetProperty(x => x.Version, x => x.Version + 1), ct);
        if (affected != 1) throw new DomainException("insufficient_stock", "Inventario insuficiente: " + sku);
        var reservation = new InventoryReservation { PartnerId = order.PartnerId, OrderId = order.Id, StockId = stock.Id, Sku = sku, Quantity = quantity, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15) };
        db.InventoryReservations.Add(reservation);
        return reservation.Id.ToString();
    }, null, ct);
    public async Task Release(string reservationReference, CancellationToken ct = default)
    {
        var id = Guid.Parse(reservationReference);
        var reservation = await db.InventoryReservations.SingleAsync(x => x.Id == id, ct);
        if (reservation.Status != "active") return;
        var affected = await db.Stocks.Where(x => x.Id == reservation.StockId && x.Reserved >= reservation.Quantity).ExecuteUpdateAsync(s => s.SetProperty(x => x.Reserved, x => x.Reserved - reservation.Quantity).SetProperty(x => x.Version, x => x.Version + 1), ct);
        if (affected != 1) throw new DomainException("reservation_conflict", "Reserva inconsistente.");
        reservation.Status = "released";
    }
    public Task<AdapterHealth> CheckHealth(CancellationToken ct = default) => guard.Execute(System, "health", () => Task.FromResult(new AdapterHealth(System, true, 0, "simulated", null)), null, ct);
}

public sealed class CrmCustomerAdapter(IntegrationDbContext db, SimulatorGuard guard) : ICustomerAdapter
{
    public async Task ValidateCustomer(string externalId, Guid partnerId, CancellationToken ct) => await guard.Execute("CRM", "validate-customer", async () =>
    {
        if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > 100) throw new DomainException("invalid_customer", "Referencia de cliente inválida.");
        var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.ExternalId == externalId && x.PartnerId == partnerId, ct) ?? throw new DomainException("customer_not_found", "El cliente no existe en el CRM simulado para este partner.");
        return new { ExternalAccount = customer.ExternalId, DisplayLabel = customer.DisplayName };
    }, partnerId, ct);
}

public sealed class OmsOrderAdapter(IntegrationDbContext db, SimulatorGuard guard) : IOrderManagementAdapter
{
    public async Task SynchronizeOrder(Guid orderId, CancellationToken ct)
    {
        await guard.Execute("OMS", "sync-order", async () =>
        {
            var order = await db.Orders.AsNoTracking().Include(x => x.Items).SingleAsync(x => x.Id == orderId, ct);
            // Mapping to the simulated OMS wire model is intentionally isolated from the application.
            var external = new { ExternalOrderNumber = order.Id.ToString("N"), FulfillmentState = order.Status, Lines = order.Items.Select(x => new { ItemCode = x.Sku, Units = x.Quantity }) };
            var replica = await db.OmsReplicas.SingleOrDefaultAsync(x => x.OrderId == order.Id, ct);
            if (replica is null) { replica = new OmsReplica { OrderId = order.Id, PartnerId = order.PartnerId, ChannelId = order.ChannelId }; db.OmsReplicas.Add(replica); }
            replica.ExternalOrderNumber = external.ExternalOrderNumber; replica.FulfillmentState = external.FulfillmentState;
            replica.LinesJson = System.Text.Json.JsonSerializer.Serialize(external.Lines); replica.SynchronizedAt = DateTimeOffset.UtcNow;
            return replica;
        }, null, ct);
        await db.SaveChangesAsync(ct);
    }
}

public sealed class SimulatedPaymentGateway(IntegrationDbContext db, SimulatorGuard guard) : IPaymentGateway
{
    public string Provider => "simulator";
    public Task<PaymentResult> Initiate(PaymentRequest request, CancellationToken ct = default) => guard.Execute("Payment Gateway", "initiate-payment", () =>
    {
        var outcome = request.SimulationOutcome.ToLowerInvariant();
        if (outcome == "unavailable") throw new HttpRequestException("Pasarela simulada no disponible.");
        if (outcome is not ("approved" or "rejected" or "pending")) throw new DomainException("invalid_outcome", "Resultado de pago inválido.");
        return Task.FromResult(new PaymentResult($"sim-{request.OrderId:N}", outcome, Provider, true));
    }, request.PartnerId, ct);
    public async Task<PaymentResult> GetStatus(string reference, CancellationToken ct = default)
    {
        var payment = await db.Payments.AsNoTracking().SingleAsync(x => x.Reference == reference, ct);
        return new PaymentResult(payment.Reference, payment.Status, Provider, true);
    }
    public async Task Refund(string reference, string idempotencyKey, CancellationToken ct = default)
    {
        var payment = await db.Payments.SingleAsync(x => x.Reference == reference, ct);
        if (payment.Status == "refunded") return;
        if (payment.Status != "approved") throw new DomainException("invalid_refund", "Solo pagos aprobados pueden reembolsarse.");
        payment.Status = "refunded";
        await db.SaveChangesAsync(ct);
    }
}
