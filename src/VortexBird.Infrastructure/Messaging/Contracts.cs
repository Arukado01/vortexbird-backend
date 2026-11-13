using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VortexBird.Domain.Entities;

namespace VortexBird.Infrastructure.Messaging;

public sealed record IntegrationEnvelope(Guid Id, Guid PartnerId, string Type, int Version, string Payload, string CorrelationId, DateTimeOffset CreatedAt)
{
    public static IntegrationEnvelope From(OutboxMessage message) => new(message.Id, message.PartnerId, message.Type, message.Version, message.Payload, message.CorrelationId, message.CreatedAt);
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));
    public void Validate()
    {
        if (Id == Guid.Empty || PartnerId == Guid.Empty || Version != 1 || string.IsNullOrWhiteSpace(Type) || Type.Length > 120 || Payload is null || Encoding.UTF8.GetByteCount(Payload) > 128000 || CorrelationId is null || CorrelationId.Length > 128 || Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(this)) > 240000) throw new InvalidDataException("Invalid integration envelope.");
        using var document = JsonDocument.Parse(Payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Event data must be an object.");
    }
}
public sealed record ReceivedEvent(string ReceiptHandle, string Body, int ReceiveCount);
public interface IQueueTransport
{
    Task Publish(IntegrationEnvelope message, CancellationToken ct);
    Task<IReadOnlyList<ReceivedEvent>> Receive(CancellationToken ct);
    Task Acknowledge(string receiptHandle, CancellationToken ct);
    Task Retry(string receiptHandle, int delaySeconds, CancellationToken ct);
}
public enum InboxDisposition { Acquired, AlreadyCompleted, Busy, DeadLetter }
public sealed record InboxClaim(InboxDisposition Disposition, string LeaseId, int Attempts);
public interface IInboxStore
{
    Task<InboxClaim> Claim(IntegrationEnvelope message, TimeSpan lease, CancellationToken ct);
    Task<bool> Complete(IntegrationEnvelope message, string leaseId, CancellationToken ct);
    Task<bool> Fail(IntegrationEnvelope message, string leaseId, int attempts, int maxAttempts, CancellationToken ct);
}
public interface IIntegrationEventHandler { Task Handle(IntegrationEnvelope message, CancellationToken ct); }

public sealed class MessagingInbox
{
    public Guid EventId { get; set; }
    public string Consumer { get; set; } = "integration-router-v1";
    public Guid PartnerId { get; set; }
    public string PayloadHash { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string LeaseId { get; set; } = "";
    public DateTimeOffset LeaseUntil { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public int Attempts { get; set; }
}
public static class MessagingModelConfiguration
{
    public static void ConfigureMessaging(ModelBuilder builder)
    {
        builder.Entity<MessagingInbox>().ToTable("MessagingInbox");
        builder.Entity<MessagingInbox>().HasKey(x => new { x.EventId, x.Consumer });
        builder.Entity<MessagingInbox>().HasIndex(x => new { x.Status, x.LeaseUntil });
        builder.Entity<MessagingInbox>().Property(x => x.PayloadHash).HasMaxLength(64);
        builder.Entity<MessagingInbox>().Property(x => x.LeaseId).HasMaxLength(32);
    }
}
public sealed class MessagingOptions
{
    public string Provider { get; init; } = "local";
    public string QueueUrl { get; init; } = "";
    public string? ServiceUrl { get; init; }
    public string Region { get; init; } = "us-east-2";
    public int VisibilityTimeoutSeconds { get; init; } = 120;
    public int MaxAttempts { get; init; } = 5;
    public bool IsSqs => Provider == "sqs";
}
