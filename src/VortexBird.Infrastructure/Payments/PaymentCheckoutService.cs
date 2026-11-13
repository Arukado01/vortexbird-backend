using Microsoft.EntityFrameworkCore;
using VortexBird.Application.Abstractions;
using VortexBird.Application.Contracts;
using VortexBird.Domain;
using VortexBird.Domain.Entities;

namespace VortexBird.Infrastructure.Payments;

public sealed record PaymentRuntimeOptions(bool Enabled, string WebhookSecret);
public sealed record PaymentCheckoutView(Guid OrderId, string Provider, string Status, string? CheckoutUrl, DateTimeOffset? ExpiresAt, string PaymentStatus, bool CanRetry = false);

public sealed class PaymentCheckoutService(IntegrationDbContext db, MercadoPagoSandboxClient client, PaymentRuntimeOptions options)
{
    public const string Provider = "mercadopago-sandbox";
    public static string LocalReference(Guid orderId) => $"checkout:{orderId:N}";
    public static SandboxCheckoutRequest Expected(PaymentCheckout checkout) => new(checkout.OrderId, checkout.PartnerId, checkout.ExpectedAmount, checkout.Currency, checkout.ExpiresAt);

    public async Task<PaymentResult> Initiate(PaymentRequest request, CancellationToken ct)
    {
        if (!options.Enabled) throw new InvalidOperationException("El proveedor sandbox está deshabilitado.");
        var lease = Guid.NewGuid();
        PaymentCheckout checkout;
        var create = false;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await LockOrder(db, request.OrderId, ct);
            var order = await db.Orders.SingleAsync(x => x.Id == request.OrderId, ct);
            if (order.PartnerId != request.PartnerId || order.Total != request.Amount || order.Currency != request.Currency)
                throw new DomainException("payment_mismatch", "La intención no coincide con la orden persistida.");
            checkout = (await db.Set<PaymentCheckout>().SingleOrDefaultAsync(x => x.OrderId == order.Id, ct))!;
            if (checkout is null)
            {
                if (order.Status != OrderStates.Reserved) return new PaymentResult(LocalReference(order.Id), "cancelled", Provider, false);
                var reservations = await db.InventoryReservations.Where(x => x.OrderId == order.Id && x.Status == "active").ToListAsync(ct);
                if (reservations.Count == 0 || reservations.Any(x => x.ExpiresAt <= DateTimeOffset.UtcNow))
                    throw new DomainException("reservation_expired", "La reserva expiró antes del checkout.");
                var now = DateTimeOffset.UtcNow;
                checkout = new PaymentCheckout
                {
                    OrderId = order.Id, PartnerId = order.PartnerId, ChannelId = order.ChannelId,
                    ExpectedAmount = order.Total, Currency = order.Currency,
                    ExternalReference = MercadoPagoSandboxClient.Reference(new(order.Id, order.PartnerId, order.Total, order.Currency)),
                    ExpiresAt = reservations.Min(x => x.ExpiresAt), LeaseId = lease, LeaseUntil = now.AddMinutes(2), NextAttemptAt = now.AddMinutes(2)
                };
                db.Set<PaymentCheckout>().Add(checkout);
                db.Payments.Add(new Payment { OrderId = order.Id, PartnerId = order.PartnerId, Provider = Provider, Reference = LocalReference(order.Id), Amount = order.Total, Status = "pending" });
                create = true;
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        // The committed intent precedes the remote mutation. A lost response MUST NOT create a second preference.
        if (create)
        {
            try
            {
                var remote = await client.CreateCheckout(Expected(checkout), ct);
                await db.Set<PaymentCheckout>().Where(x => x.OrderId == checkout.OrderId && x.LeaseId == lease)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.PreferenceId, remote.PreferenceId)
                        .SetProperty(x => x.CheckoutUrl, remote.CheckoutUrl.AbsoluteUri)
                        .SetProperty(x => x.State, x => x.State == "creating" || x.State == "unknown" ? "pending" : x.State)
                        .SetProperty(x => x.LeaseId, (Guid?)null).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
                        .SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or TaskCanceledException)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await db.Set<PaymentCheckout>().Where(x => x.OrderId == checkout.OrderId && x.LeaseId == lease)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, x => x.State == "creating" ? "unknown" : x.State)
                        .SetProperty(x => x.LastError, ex.GetType().Name).SetProperty(x => x.LeaseId, (Guid?)null)
                        .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null).SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow), cleanup.Token);
                if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
            }
            finally { db.ChangeTracker.Clear(); }
        }
        return new PaymentResult(LocalReference(request.OrderId), "pending", Provider, false);
    }

    public async Task<PaymentCheckoutView> View(Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().SingleAsync(x => x.Id == orderId, ct);
        var checkout = await db.Set<PaymentCheckout>().AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == orderId, ct);
        if (checkout is null)
        {
            var provider = await db.Payments.Where(x => x.OrderId == orderId).Select(x => x.Provider).SingleOrDefaultAsync(ct);
            provider ??= options.Enabled ? Provider : "simulator";
            return new(orderId, provider, provider == "simulator" ? "simulated" : "creating", null, null, order.PaymentStatus);
        }
        var url = checkout.State == "pending" && checkout.ExpiresAt > DateTimeOffset.UtcNow && order.Status is OrderStates.Reserved or OrderStates.AwaitingPayment ? checkout.CheckoutUrl : null;
        return new(orderId, Provider, checkout.State, url, checkout.ExpiresAt, order.PaymentStatus);
    }

    public async Task RequestReconciliation(Guid orderId, CancellationToken ct)
    {
        if (!options.Enabled) throw new DomainException("sandbox_disabled", "La conciliación externa está deshabilitada.");
        var count = await db.Set<PaymentCheckout>().Where(x => x.OrderId == orderId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow).SetProperty(x => x.Attempts, 0).SetProperty(x => x.ReconcileRequested, true).SetProperty(x => x.LastError, (string?)null), ct);
        if (count == 0) throw new DomainException("checkout_missing", "Aún no existe una intención externa persistida.");
    }

    public static Task<int> LockOrder(IntegrationDbContext db, Guid orderId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({orderId.ToString()}, 0))", ct);
}

public sealed class MercadoPagoSandboxGateway(PaymentCheckoutService checkouts, IntegrationDbContext db, MercadoPagoSandboxClient client) : IPaymentGateway
{
    public string Provider => PaymentCheckoutService.Provider;
    public Task<PaymentResult> Initiate(PaymentRequest request, CancellationToken cancellationToken = default) => checkouts.Initiate(request, cancellationToken);
    public async Task<PaymentResult> GetStatus(string reference, CancellationToken cancellationToken = default)
    {
        var orderId = ParseReference(reference);
        var checkout = await db.Set<PaymentCheckout>().AsNoTracking().SingleAsync(x => x.OrderId == orderId, cancellationToken);
        if (checkout.AcceptedPaymentId is null) return new(reference, "pending", Provider, false);
        var payment = await client.ReadVerifiedPayment(checkout.AcceptedPaymentId, PaymentCheckoutService.Expected(checkout), cancellationToken);
        return new(reference, payment.Status, Provider, false);
    }
    public async Task Refund(string reference, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var orderId = ParseReference(reference);
        var checkout = await db.Set<PaymentCheckout>().AsNoTracking().SingleAsync(x => x.OrderId == orderId, cancellationToken);
        if (checkout.AcceptedPaymentId is null) throw new InvalidOperationException("No hay pago verificado para reembolsar.");
        var result = await client.RefundVerifiedPayment(checkout.AcceptedPaymentId, PaymentCheckoutService.Expected(checkout), idempotencyKey, cancellationToken);
        if (result.Status != "refunded") throw new InvalidOperationException("El reembolso aún requiere conciliación.");
    }
    private static Guid ParseReference(string reference) => reference.StartsWith("checkout:", StringComparison.Ordinal) && Guid.TryParseExact(reference[9..], "N", out var id)
        ? id : throw new ArgumentException("Referencia de checkout inválida.", nameof(reference));
}
