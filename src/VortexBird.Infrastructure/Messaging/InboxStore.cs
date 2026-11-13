using Microsoft.EntityFrameworkCore;

namespace VortexBird.Infrastructure.Messaging;

public sealed class PostgresInboxStore(IntegrationDbContext db) : IInboxStore
{
    private const string Consumer = "integration-router-v1";
    public async Task<InboxClaim> Claim(IntegrationEnvelope message, TimeSpan lease, CancellationToken ct)
    {
        message.Validate();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"inbox:" + message.Id}, 0))", ct);
        // Validate origin BEFORE reserving the identifier: forged bus data must never poison the inbox hash.
        var canonical = await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == message.Id && x.PartnerId == message.PartnerId, ct) ?? throw new InvalidDataException("Event is not in the trusted outbox.");
        if (IntegrationEnvelope.From(canonical).Fingerprint() != message.Fingerprint()) throw new InvalidDataException("Event content differs from the trusted outbox.");
        var row = await db.Set<MessagingInbox>().SingleOrDefaultAsync(x => x.EventId == message.Id && x.Consumer == Consumer, ct);
        var hash = message.Fingerprint();
        if (row is not null && row.PayloadHash != hash) throw new InvalidDataException("Event identifier reused with different content.");
        if (row?.Status == "completed")
        {
            await db.OutboxMessages.Where(x => x.Id == message.Id && x.PartnerId == message.PartnerId && x.Status != "processed").ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "processed").SetProperty(x => x.ProcessedAt, row.CompletedAt).SetProperty(x => x.LastError, (string?)null), ct);
            await tx.CommitAsync(ct);
            return new InboxClaim(InboxDisposition.AlreadyCompleted, row.LeaseId, row.Attempts);
        }
        if (row?.Status == "dead_letter") return new InboxClaim(InboxDisposition.DeadLetter, row.LeaseId, row.Attempts);
        if (row?.Status == "processing" && row.LeaseUntil > DateTimeOffset.UtcNow) return new InboxClaim(InboxDisposition.Busy, row.LeaseId, row.Attempts);
        if (row is null) { row = new MessagingInbox { EventId = message.Id, PartnerId = message.PartnerId, PayloadHash = hash }; db.Set<MessagingInbox>().Add(row); }
        row.Status = "processing"; row.LeaseId = Guid.NewGuid().ToString("N"); row.LeaseUntil = DateTimeOffset.UtcNow.Add(lease); row.Attempts++;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new InboxClaim(InboxDisposition.Acquired, row.LeaseId, row.Attempts);
    }
    public async Task<bool> Complete(IntegrationEnvelope message, string leaseId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.Set<MessagingInbox>().Where(x => x.EventId == message.Id && x.Consumer == Consumer && x.LeaseId == leaseId && x.Status == "processing").ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "completed").SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow), ct);
        if (changed == 1)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"ProcessedMessages\" (\"Id\",\"Consumer\",\"ProcessedAt\") VALUES ({message.Id},{Consumer},{DateTimeOffset.UtcNow}) ON CONFLICT DO NOTHING", ct);
            // Definitive consumer success dominates an ambiguous publisher timeout or DLQ state.
            await db.OutboxMessages.Where(x => x.Id == message.Id && x.PartnerId == message.PartnerId && x.Status != "processed").ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "processed").SetProperty(x => x.ProcessedAt, DateTimeOffset.UtcNow).SetProperty(x => x.LastError, (string?)null), ct);
        }
        await tx.CommitAsync(ct); return changed == 1;
    }
    public async Task<bool> Fail(IntegrationEnvelope message, string leaseId, int attempts, int maxAttempts, CancellationToken ct)
    {
        var status = attempts >= maxAttempts ? "dead_letter" : "pending";
        var changed = await db.Set<MessagingInbox>().Where(x => x.EventId == message.Id && x.Consumer == Consumer && x.LeaseId == leaseId && x.Status == "processing").ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status).SetProperty(x => x.LeaseUntil, DateTimeOffset.UtcNow), ct);
        if (changed == 1 && status == "dead_letter") await db.OutboxMessages.Where(x => x.Id == message.Id && x.Status == "published").ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "dead_letter").SetProperty(x => x.LastError, "Consumer retries exhausted."), ct);
        return changed == 1;
    }
}

public sealed class DurableEventConsumer(IInboxStore inbox, IIntegrationEventHandler handler, MessagingOptions options)
{
    public async Task<bool> Process(IntegrationEnvelope message, CancellationToken ct)
    {
        var claim = await inbox.Claim(message, TimeSpan.FromSeconds(options.VisibilityTimeoutSeconds), ct);
        if (claim.Disposition == InboxDisposition.AlreadyCompleted) return true;
        if (claim.Disposition is InboxDisposition.Busy or InboxDisposition.DeadLetter) return false;
        try
        {
            // Keep work bounded below SQS visibility; uncompleted work returns to the queue.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.VisibilityTimeoutSeconds - 15)));
            await handler.Handle(message, deadline.Token);
            return await inbox.Complete(message, claim.LeaseId, ct);
        }
        catch
        {
            // A lost lease never marks a different worker's execution failed.
            if (!ct.IsCancellationRequested) await inbox.Fail(message, claim.LeaseId, claim.Attempts, options.MaxAttempts, ct);
            throw;
        }
    }
}

public static class QueueAcknowledgement
{
    public static async Task Process(ReceivedEvent received, IQueueTransport transport, DurableEventConsumer consumer, CancellationToken ct)
    {
        try
        {
            var message = System.Text.Json.JsonSerializer.Deserialize<IntegrationEnvelope>(received.Body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing event envelope.");
            if (await consumer.Process(message, ct)) await transport.Acknowledge(received.ReceiptHandle, ct);
            else await transport.Retry(received.ReceiptHandle, 15, ct);
        }
        catch when (!ct.IsCancellationRequested)
        {
            // Never delete poison messages: configured SQS redrive policy moves them to its DLQ.
            await transport.Retry(received.ReceiptHandle, Math.Min(300, (int)Math.Pow(2, Math.Clamp(received.ReceiveCount, 1, 8))), ct);
            throw;
        }
    }
}
