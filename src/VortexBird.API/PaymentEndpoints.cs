using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VortexBird.Domain.Entities;
using VortexBird.Infrastructure;
using VortexBird.Infrastructure.Payments;

namespace VortexBird.API;

public static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/payments/orders/{orderId:guid}/checkout", async (Guid orderId, IntegrationDbContext db, HttpActor actor, PaymentCheckoutService service, CancellationToken ct) =>
        {
            await actor.Require("payments.read", ct);
            var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == orderId, ct) ?? throw new MissingEntityException("Orden no encontrada.");
            await actor.EnsurePartnerAccess(order.PartnerId, order.ChannelId, ct);
            return Results.Ok(await service.View(orderId, ct));
        });
        api.MapPost("/payments/orders/{orderId:guid}/reconcile", async (Guid orderId, IntegrationDbContext db, HttpActor actor, PaymentCheckoutService service, CancellationToken ct) =>
        {
            await actor.Require("payments.manage", ct);
            var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == orderId, ct) ?? throw new MissingEntityException("Orden no encontrada.");
            await actor.EnsurePartnerAccess(order.PartnerId, order.ChannelId, ct);
            await service.RequestReconciliation(orderId, ct);
            return Results.Accepted($"/api/v1/payments/orders/{orderId}/checkout", await service.View(orderId, ct));
        });
        api.MapGet("/payments/inbox", async (IntegrationDbContext db, HttpActor actor, HttpRequest request, CancellationToken ct) =>
        {
            await actor.Require("payments.manage", ct); actor.RequireGlobal(); actor.RequireUnrestrictedChannel();
            return await OperationalEndpoints.Page(db.Set<PaymentInbox>().AsNoTracking().Where(x => x.Status == "dead_letter").OrderBy(x => x.CreatedAt)
                .Select(x => new { x.Id, x.ProviderPaymentId, x.Status, x.Attempts, x.LastError, x.CreatedAt }), request, ct);
        });
        api.MapPost("/payments/inbox/{id:guid}/retry", async (Guid id, IntegrationDbContext db, HttpActor actor, PaymentRuntimeOptions options, CancellationToken ct) =>
        {
            await actor.Require("payments.manage", ct); actor.RequireGlobal(); actor.RequireUnrestrictedChannel();
            if (!options.Enabled) return Results.Conflict(new { code = "sandbox_disabled" });
            var count = await db.Set<PaymentInbox>().Where(x => x.Id == id && x.Status == "dead_letter").ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, "pending").SetProperty(x => x.Attempts, 0).SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow).SetProperty(x => x.LastError, (string?)null), ct);
            return count == 1 ? Results.Accepted() : Results.NotFound();
        });
    }

    public static void MapMercadoPagoWebhook(this WebApplication app)
    {
        app.MapPost("/api/v1/payments/mercadopago/webhook", Receive).AllowAnonymous();
    }

    private static async Task<IResult> Receive(HttpRequest request, PaymentRuntimeOptions runtime, MercadoPagoSandboxOptions provider, IntegrationDbContext db, CancellationToken ct)
    {
        if (!runtime.Enabled) return Results.NotFound();
        var signature = request.Headers["x-signature"].ToString();
        var requestId = request.Headers["x-request-id"].ToString();
        var paymentId = request.Query["data.id"].ToString();
        if (!MercadoPagoWebhookSignature.Validate(signature, requestId, paymentId, runtime.WebhookSecret, DateTimeOffset.UtcNow)) return Results.Unauthorized();
        if (request.ContentLength > 32768) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (body.Length + count > 32768) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            body.Write(buffer, 0, count);
        }
        var notification = PaymentWebhookReceiptParser.Parse(Encoding.UTF8.GetString(body.ToArray()), signature, requestId, paymentId, runtime.WebhookSecret, provider.ExpectedTestSellerId, DateTimeOffset.UtcNow);
        if (notification is null) return Results.BadRequest(new { code = "invalid_notification" });
        db.Set<PaymentInbox>().Add(new PaymentInbox { ProviderPaymentId = notification.PaymentId, NotificationKey = notification.NotificationKey });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_PaymentInbox_NotificationKey" }) { db.ChangeTracker.Clear(); }
        // Acknowledge only AFTER durable insert (or an already durable duplicate), before any provider request.
        return Results.Ok(new { received = true });
    }
}
