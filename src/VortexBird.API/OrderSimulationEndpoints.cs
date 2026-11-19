using Microsoft.EntityFrameworkCore;
using VortexBird.Application.Contracts;
using VortexBird.Domain;
using VortexBird.Infrastructure;

namespace VortexBird.API;

public static class OrderSimulationEndpoints
{
    public static bool IsEnabled(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment()
        && (configuration["Payments:Provider"] ?? "Simulator") == "Simulator";

    public static void MapOrderSimulationEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/simulations/orders", async (SimulatedOrderInput input, HttpRequest request, CreateOrderHandler handler, HttpActor actor, CancellationToken ct) =>
        {
            await actor.Require("orders.create", ct);
            if (input.PaymentOutcome is null || !AllowedOutcomes.Contains(input.PaymentOutcome))
                throw new RequestInputException("Selecciona approved, rejected, pending o unavailable.");
            var order = new CreateOrderRequest(input.PartnerId, input.ChannelId, input.StoreId, input.CustomerId, input.Items);
            var receipt = await handler.Handle(new CreateOrderCommand(order, request.Headers["Idempotency-Key"].ToString(), input.PaymentOutcome), ct);
            return Results.Accepted($"/api/v1/orders/{receipt.Id}", receipt);
        }).ExcludeFromDescription();

        api.MapPost("/payments/{id:guid}/simulate", async (Guid id, PaymentOutcomeInput input, IntegrationDbContext db, HttpActor actor, OrderSaga saga, CancellationToken ct) =>
        {
            await actor.Require("payments.manage", ct);
            var payment = await db.Payments.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new MissingEntityException("Pago no encontrado.");
            var order = await db.Orders.SingleAsync(x => x.Id == payment.OrderId, ct);
            await actor.EnsurePartnerAccess(order.PartnerId, order.ChannelId, ct);
            await saga.Resolve(payment.OrderId, input.Outcome, ct);
            return Results.Ok(new { status = input.Outcome });
        }).ExcludeFromDescription();
    }

    private static readonly HashSet<string> AllowedOutcomes = new(StringComparer.OrdinalIgnoreCase)
    {
        "approved", "rejected", "pending", "unavailable"
    };

    private sealed record SimulatedOrderInput(Guid PartnerId, Guid ChannelId, string StoreId, string CustomerId, List<OrderLineRequest> Items, string PaymentOutcome = "approved");
    private sealed record PaymentOutcomeInput(string Outcome);
}
