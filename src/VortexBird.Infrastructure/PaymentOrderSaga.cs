using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VortexBird.Domain;
using VortexBird.Domain.Entities;
using VortexBird.Infrastructure.Payments;

namespace VortexBird.Infrastructure;

public sealed partial class OrderSaga
{
    /// <summary>Called only with server-fetched sandbox evidence. All settlement effects share the order transaction.</summary>
    public async Task<bool> ApplyVerifiedPayment(Guid orderId, VerifiedSandboxPayment evidence, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PaymentCheckoutService.LockOrder(db, orderId, ct);
        var checkout = await db.Set<PaymentCheckout>().SingleAsync(x => x.OrderId == orderId, ct);
        MercadoPagoSandboxClient.AssertMatches(evidence, PaymentCheckoutService.Expected(checkout));
        var order = await db.Orders.Include(x => x.Items).Include(x => x.Timeline).SingleAsync(x => x.Id == orderId, ct);
        var payment = await db.Payments.SingleAsync(x => x.OrderId == orderId, ct);
        if (payment.Provider != PaymentCheckoutService.Provider) throw new InvalidOperationException("La orden no usa el proveedor sandbox.");
        var receipt = await db.Set<PaymentReceipt>().SingleOrDefaultAsync(x => x.ProviderPaymentId == evidence.PaymentId, ct);
        if (receipt is not null && receipt.OrderId != orderId) throw new InvalidOperationException("El identificador de pago pertenece a otra orden.");
        var previous = receipt?.ProviderStatus;
        if (receipt is null)
        {
            receipt = new PaymentReceipt { ProviderPaymentId = evidence.PaymentId, OrderId = orderId };
            db.Set<PaymentReceipt>().Add(receipt);
        }
        var now = DateTimeOffset.UtcNow;
        var reservations = await db.InventoryReservations.Where(x => x.OrderId == orderId && x.Status == "active").ToListAsync(ct);
        var valid = checkout.ExpiresAt > now && reservations.Count > 0 && reservations.All(x => x.ExpiresAt > now)
            && order.Items.All(line => reservations.Where(r => r.Sku == line.Sku).Sum(r => r.Quantity) == line.Quantity)
            && reservations.All(r => order.Items.Any(line => line.Sku == r.Sku));
        var action = PaymentOutcomePolicy.Decide(evidence.Status, order.Status, checkout.AcceptedPaymentId, evidence.PaymentId, previous, valid);
        switch (action)
        {
            case PaymentAction.Confirm:
                if (order.Status == OrderStates.Reserved) order.TransitionTo(OrderStates.AwaitingPayment, "Confirmación de checkout sandbox recibida.", now);
                await ApplyOutcome(order, payment, "approved", ct);
                checkout.AcceptedPaymentId = evidence.PaymentId;
                checkout.State = "approved";
                receipt.Disposition = "accepted";
                break;
            case PaymentAction.Cancel:
                if (order.Status == OrderStates.Reserved) order.TransitionTo(OrderStates.AwaitingPayment, "Resultado de checkout sandbox recibido.", now);
                await ApplyOutcome(order, payment, "rejected", ct);
                checkout.State = "cancelled";
                receipt.Disposition = "rejected";
                break;
            case PaymentAction.Refund:
                if (order.Status is OrderStates.Reserved or OrderStates.AwaitingPayment)
                {
                    foreach (var reservation in reservations) await inventory.Release(reservation.Id.ToString(), ct);
                    order.TransitionTo(OrderStates.Compensating, "Pago tardío o reserva inválida; reembolso en conciliación.", now);
                    order.TransitionTo(OrderStates.Cancelled, "Reservas liberadas; no se realizará fulfillment.", now);
                }
                if (checkout.AcceptedPaymentId is null)
                {
                    checkout.State = "refund_pending";
                    payment.Status = order.PaymentStatus = "refund_pending";
                }
                // A duplicate approved payment is refunded independently; the accepted order stays confirmed.
                if (receipt.Disposition != "refund_pending") AddPaymentEvent(order, "payment.refund_pending", evidence.PaymentId);
                receipt.Disposition = "refund_pending";
                break;
            case PaymentAction.RecordRefund:
                receipt.Disposition = "refunded";
                if (checkout.AcceptedPaymentId is null || checkout.AcceptedPaymentId == evidence.PaymentId)
                {
                    if (order.Status == OrderStates.Confirmed)
                    {
                        // No shipment started: reverse each previously committed reservation exactly once.
                        var committed = await db.InventoryReservations.Where(x => x.OrderId == orderId && x.Status == "committed").ToListAsync(ct);
                        foreach (var reservation in committed)
                        {
                            await db.Stocks.Where(x => x.Id == reservation.StockId).ExecuteUpdateAsync(s => s.SetProperty(x => x.OnHand, x => x.OnHand + reservation.Quantity).SetProperty(x => x.Version, x => x.Version + 1), ct);
                            reservation.Status = "restocked";
                        }
                        order.TransitionTo(OrderStates.Compensating, "Reembolso verificado antes del despacho.", now);
                        order.TransitionTo(OrderStates.Cancelled, "Inventario restituido.", now);
                    }
                    else if (order.Status is OrderStates.Reserved or OrderStates.AwaitingPayment)
                    {
                        foreach (var reservation in reservations) await inventory.Release(reservation.Id.ToString(), ct);
                        order.TransitionTo(OrderStates.Compensating, "Reembolso externo verificado.", now);
                        order.TransitionTo(OrderStates.Cancelled, "Reserva liberada.", now);
                    }
                    checkout.State = order.Status is OrderStates.Shipped or OrderStates.Delivered ? "manual_review" : "refunded";
                    payment.Status = order.PaymentStatus = "refunded";
                }
                AddPaymentEvent(order, "payment.refunded", evidence.PaymentId);
                break;
            case PaymentAction.ReviewChargeback:
                receipt.Disposition = "manual_review";
                if (checkout.AcceptedPaymentId is null || checkout.AcceptedPaymentId == evidence.PaymentId)
                {
                    checkout.State = "manual_review";
                    payment.Status = order.PaymentStatus = "charged_back";
                }
                AddPaymentEvent(order, "payment.review_required", evidence.PaymentId);
                break;
        }
        // A delayed pending/rejected callback must never overwrite an approved/refunded receipt.
        if (action != PaymentAction.Ignore || previous is null || previous is "pending" or "in_process" or "authorized") receipt.ProviderStatus = evidence.Status;
        receipt.UpdatedAt = checkout.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return receipt.Disposition == "refund_pending";
    }

    private void AddPaymentEvent(Order order, string type, string providerPaymentId) => db.OutboxMessages.Add(new OutboxMessage
    {
        PartnerId = order.PartnerId, Type = type, CorrelationId = order.CorrelationId,
        Payload = JsonSerializer.Serialize(new { orderId = order.Id, providerPaymentId, order.PaymentStatus })
    });
}
