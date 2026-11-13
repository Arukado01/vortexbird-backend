using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VortexBird.Domain.Entities;

namespace VortexBird.Infrastructure.Payments;

public sealed class PaymentReconciler(IntegrationDbContext db, MercadoPagoSandboxClient client, OrderSaga saga)
{
    public async Task ProcessNotification(string providerPaymentId, CancellationToken ct)
    {
        var evidence = await client.ReadSandboxPayment(providerPaymentId, ct);
        var checkout = await db.Set<PaymentCheckout>().AsNoTracking().SingleOrDefaultAsync(x => x.ExternalReference == evidence.ExternalReference, ct)
            ?? throw new InvalidOperationException("El pago notificado no corresponde a un checkout persistido.");
        await Settle(checkout, evidence, ct);
    }

    public async Task ReconcileOrder(Guid orderId, Guid lease, CancellationToken ct)
    {
        var checkout = await db.Set<PaymentCheckout>().AsNoTracking().SingleAsync(x => x.OrderId == orderId, ct);
        if (checkout.PreferenceId is null)
        {
            // Search recovers a timed-out POST, including creation before a process crash. Never POST again.
            var remote = await client.SearchCheckout(PaymentCheckoutService.Expected(checkout), ct);
            if (remote is not null)
                await db.Set<PaymentCheckout>().Where(x => x.OrderId == orderId && x.LeaseId == lease).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.PreferenceId, remote.PreferenceId).SetProperty(x => x.CheckoutUrl, remote.CheckoutUrl.AbsoluteUri)
                    .SetProperty(x => x.State, x => x.State == "creating" || x.State == "unknown" ? "pending" : x.State), ct);
            else
                await db.Set<PaymentCheckout>().Where(x => x.OrderId == orderId && x.LeaseId == lease && x.State == "creating")
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "unknown"), ct);
        }
        var evidence = await client.SearchPayments(PaymentCheckoutService.Expected(checkout), ct);
        foreach (var payment in evidence) await Settle(checkout, payment, ct);
        // A previously observed refund remains recoverable even if provider search temporarily omits it.
        var refunds = await db.Set<PaymentReceipt>().AsNoTracking().Where(x => x.OrderId == orderId && x.Disposition == "refund_pending")
            .Select(x => x.ProviderPaymentId).ToListAsync(ct);
        foreach (var id in refunds.Except(evidence.Select(x => x.PaymentId)))
            await Settle(checkout, await client.ReadVerifiedPayment(id, PaymentCheckoutService.Expected(checkout), ct), ct);
    }

    private async Task Settle(PaymentCheckout checkout, VerifiedSandboxPayment evidence, CancellationToken ct)
    {
        MercadoPagoSandboxClient.AssertMatches(evidence, PaymentCheckoutService.Expected(checkout));
        if (!await saga.ApplyVerifiedPayment(checkout.OrderId, evidence, ct)) return;
        // Refund intent was committed above. A crash before/after POST is replayed with the SAME key.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"sandbox-refund:{checkout.OrderId:N}:{evidence.PaymentId}"))).ToLowerInvariant();
        var refunded = await client.RefundVerifiedPayment(evidence.PaymentId, PaymentCheckoutService.Expected(checkout), key, ct);
        await saga.ApplyVerifiedPayment(checkout.OrderId, refunded, ct);
    }
}
