using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VortexBird.Application.Abstractions;
using VortexBird.Domain.Entities;

namespace VortexBird.Infrastructure.Messaging;

public sealed class IntegrationEventHandler(IntegrationDbContext db, OrderSaga saga, IOrderManagementAdapter oms) : IIntegrationEventHandler
{
    public async Task Handle(IntegrationEnvelope message, CancellationToken ct)
    {
        message.Validate();
        // This queue only carries our own outbox. Reject forged cross-tenant or changed bus payloads.
        var canonical = await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == message.Id && x.PartnerId == message.PartnerId, ct) ?? throw new InvalidDataException("Event is not in the trusted outbox.");
        if (IntegrationEnvelope.From(canonical).Fingerprint() != message.Fingerprint()) throw new InvalidDataException("Event content differs from the trusted outbox.");
        using var data = JsonDocument.Parse(message.Payload);
        if (data.RootElement.TryGetProperty("orderId", out var orderId))
        {
            var id = orderId.GetGuid();
            if (!await db.Orders.AnyAsync(x => x.Id == id && x.PartnerId == message.PartnerId, ct)) throw new InvalidDataException("Order is not owned by the event partner.");
            if (message.Type == "order.created")
            {
                // Unprocessed outbox rows from older versions used paymentOutcome.
                var hasSimulation = data.RootElement.TryGetProperty("simulatedOutcome", out var simulation)
                    || data.RootElement.TryGetProperty("paymentOutcome", out simulation);
                await saga.Advance(id, hasSimulation ? simulation.GetString() : null, ct);
            }
            if (message.Type is "order.confirmed" or "shipment.started" or "delivery.confirmed") await oms.SynchronizeOrder(id, ct);
        }
        else if (message.Type == "order.created") throw new InvalidDataException("Order event has no order identifier.");
        await OutboxWorker.Deliver(db, canonical, ct);
    }
}

public sealed class OutboxDispatcher(IntegrationDbContext db, IServiceProvider services, MessagingOptions options, ILogger<OutboxDispatcher> logger)
{
    public async Task<bool> DispatchBatch(CancellationToken ct)
    {
        List<OutboxMessage> messages;
        var now = DateTimeOffset.UtcNow;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // One message per claim avoids lease expiration behind a slow batch member.
            messages = await db.OutboxMessages.FromSqlInterpolated($"SELECT * FROM \"OutboxMessages\" WHERE (\"Status\"='pending' OR (\"Status\"='processing' AND \"NextAttemptAt\" < {now})) AND \"NextAttemptAt\" <= {now} ORDER BY \"CreatedAt\" LIMIT 1 FOR UPDATE SKIP LOCKED").ToListAsync(ct);
            foreach (var row in messages) { row.Status = "processing"; row.LeaseId = Guid.NewGuid().ToString("N"); row.NextAttemptAt = now.AddSeconds(options.VisibilityTimeoutSeconds); }
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        foreach (var row in messages)
        {
            var envelope = IntegrationEnvelope.From(row); var lease = row.LeaseId;
            var status = options.IsSqs ? "published" : "processed";
            var attempts = row.Attempts; string? error = null; var next = row.NextAttemptAt;
            try
            {
                if (options.IsSqs) await services.GetRequiredService<IQueueTransport>().Publish(envelope, ct);
                else if (!await services.GetRequiredService<DurableEventConsumer>().Process(envelope, ct)) throw new InvalidOperationException("Inbox is leased or exhausted.");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                attempts++; status = attempts >= options.MaxAttempts ? "dead_letter" : "pending";
                next = DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, attempts) + Random.Shared.NextDouble());
                error = ex.GetType().Name + ": messaging operation failed";
                logger.LogWarning(ex, "Outbox event {EventId} attempt {Attempt} failed", row.Id, attempts);
            }
            db.ChangeTracker.Clear();
            // A consumer may already have marked the source processed; the fenced update never regresses it.
            await db.OutboxMessages.Where(x => x.Id == row.Id && x.LeaseId == lease && x.Status == "processing").ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status).SetProperty(x => x.Attempts, attempts).SetProperty(x => x.NextAttemptAt, next).SetProperty(x => x.LastError, error), ct);
        }
        return messages.Count > 0;
    }
}

public sealed class SqsConsumerWorker(IServiceScopeFactory scopeFactory, IQueueTransport transport, ILogger<SqsConsumerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var message in await transport.Receive(stoppingToken))
                {
                    using var scope = scopeFactory.CreateScope();
                    try { await QueueAcknowledgement.Process(message, transport, scope.ServiceProvider.GetRequiredService<DurableEventConsumer>(), stoppingToken); }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning(ex, "SQS message retained for retry/redrive (receive {Count})", message.ReceiveCount); }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning(ex, "SQS receive failed; retrying without deleting messages"); await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        }
    }
}
