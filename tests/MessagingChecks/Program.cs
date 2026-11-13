using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VortexBird.Infrastructure;
using VortexBird.Infrastructure.Messaging;

var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
async Task Throws(Func<Task> work, string name) { try { await work(); } catch { count++; Console.WriteLine("PASS " + name); return; } throw new Exception("Expected failure: " + name); }
var envelope = new IntegrationEnvelope(Guid.NewGuid(), Guid.NewGuid(), "order.created", 1, "{\"orderId\":\"11111111-1111-1111-1111-111111111111\",\"paymentOutcome\":\"approved\"}", "test-correlation", DateTimeOffset.UtcNow);
var encoded = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
var received = new ReceivedEvent("receipt-first", encoded, 1);
var options = new MessagingOptions();

var inbox = new MemoryInbox(); var handler = new RecordingHandler(); var queue = new RecordingQueue();
var consumer = new DurableEventConsumer(inbox, handler, options);
await QueueAcknowledgement.Process(received, queue, consumer, default);
Check(handler.Calls == 1 && inbox.Completed && queue.Acks == 1, "Handle, persist completion, then acknowledge");
Check(inbox.Sequence.SequenceEqual(new[] { "claim", "complete" }), "Completion persisted after claim");
await QueueAcknowledgement.Process(received with { ReceiptHandle = "receipt-redelivery" }, queue, consumer, default);
Check(handler.Calls == 1 && queue.Acks == 2 && queue.LastReceipt == "receipt-redelivery", "Completed duplicate acknowledges latest receipt without effects");

inbox = new MemoryInbox { InitialDisposition = InboxDisposition.Busy }; handler = new RecordingHandler(); queue = new RecordingQueue();
await QueueAcknowledgement.Process(received, queue, new DurableEventConsumer(inbox, handler, options), default);
Check(handler.Calls == 0 && queue.Acks == 0 && queue.Retries == 1, "Active lease never executes or acknowledges duplicate");

inbox = new MemoryInbox { InitialDisposition = InboxDisposition.DeadLetter }; handler = new RecordingHandler(); queue = new RecordingQueue();
await QueueAcknowledgement.Process(received, queue, new DurableEventConsumer(inbox, handler, options), default);
Check(handler.Calls == 0 && queue.Acks == 0 && queue.Retries == 1, "Dead-letter inbox retains queue message for redrive");

inbox = new MemoryInbox(); handler = new RecordingHandler { Fail = true }; queue = new RecordingQueue();
await Throws(() => QueueAcknowledgement.Process(received, queue, new DurableEventConsumer(inbox, handler, options), default), "Handler failure is surfaced");
Check(inbox.Failures == 1 && !inbox.Completed && queue.Acks == 0 && queue.Retries == 1, "Handler failure persists retry and never acknowledges");

inbox = new MemoryInbox { CompletionAccepted = false }; handler = new RecordingHandler(); queue = new RecordingQueue();
await QueueAcknowledgement.Process(received, queue, new DurableEventConsumer(inbox, handler, options), default);
Check(queue.Acks == 0 && queue.Retries == 1, "Lost lease at completion cannot acknowledge");

inbox = new MemoryInbox(); handler = new RecordingHandler(); queue = new RecordingQueue { FailAck = true };
consumer = new DurableEventConsumer(inbox, handler, options);
await Throws(() => QueueAcknowledgement.Process(received, queue, consumer, default), "Network failure during ack is surfaced");
Check(inbox.Completed && handler.Calls == 1 && queue.Retries == 1, "Ack failure preserves completed inbox");
queue.FailAck = false;
await QueueAcknowledgement.Process(received with { ReceiptHandle = "receipt-after-ack-failure" }, queue, consumer, default);
Check(handler.Calls == 1 && queue.LastReceipt == "receipt-after-ack-failure", "Redelivery after ack failure does not repeat business effects");

queue = new RecordingQueue();
await Throws(() => QueueAcknowledgement.Process(received with { Body = "{invalid" }, queue, consumer, default), "Malformed message fails closed");
Check(queue.Acks == 0 && queue.Retries == 1, "Poison message is never deleted");
await Throws(() => { (envelope with { Payload = null! }).Validate(); return Task.CompletedTask; }, "Null event data rejected");
await Throws(() => { (envelope with { PartnerId = Guid.Empty }).Validate(); return Task.CompletedTask; }, "Missing partner rejected");
await Throws(() => { (envelope with { Payload = JsonSerializer.Serialize(new { text = new string('界', 60000) }) }).Validate(); return Task.CompletedTask; }, "Oversized UTF-8 envelope rejected");
Check(envelope.Fingerprint() != (envelope with { PartnerId = Guid.NewGuid() }).Fingerprint(), "Inbox fingerprint binds tenant");
Check(envelope.Fingerprint() != (envelope with { Payload = "{}" }).Fingerprint(), "Inbox fingerprint binds content");

using var db = new IntegrationDbContext(new DbContextOptionsBuilder<IntegrationDbContext>().UseNpgsql("Host=localhost;Database=metadata;Username=metadata").Options);
var entity = db.Model.FindEntityType(typeof(MessagingInbox))!;
Check(entity.FindPrimaryKey()!.Properties.Select(x => x.Name).SequenceEqual(new[] { "EventId", "Consumer" }), "Database inbox uniquely identifies event and consumer");
Check(entity.GetIndexes().Any(x => x.Properties.Select(p => p.Name).SequenceEqual(new[] { "Status", "LeaseUntil" })), "Recovery lookup has status/lease index");
Console.WriteLine($"{count}/{count} messaging orchestration checks passed. No AWS or database sockets used.");
var output = args.FirstOrDefault(x => x.StartsWith("--report=", StringComparison.Ordinal))?[9..];
if (output is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    File.WriteAllText(output, JsonSerializer.Serialize(new { measuredAt = DateTimeOffset.UtcNow, passed = count, failed = 0, scope = "Durable consumer orchestration with in-memory inbox/queue doubles and real EF model metadata; no AWS, PostgreSQL or network calls", awsContacted = false, databaseContacted = false }, new JsonSerializerOptions { WriteIndented = true }));
}

sealed class MemoryInbox : IInboxStore
{
    public InboxDisposition InitialDisposition = InboxDisposition.Acquired;
    public bool Completed, CompletionAccepted = true;
    public int Failures;
    public List<string> Sequence { get; } = [];
    public Task<InboxClaim> Claim(IntegrationEnvelope message, TimeSpan lease, CancellationToken ct) { message.Validate(); Sequence.Add("claim"); return Task.FromResult(new InboxClaim(Completed ? InboxDisposition.AlreadyCompleted : InitialDisposition, "lease-owner", 1)); }
    public Task<bool> Complete(IntegrationEnvelope message, string leaseId, CancellationToken ct) { Sequence.Add("complete"); Completed = CompletionAccepted && leaseId == "lease-owner"; return Task.FromResult(Completed); }
    public Task<bool> Fail(IntegrationEnvelope message, string leaseId, int attempts, int maxAttempts, CancellationToken ct) { Failures++; return Task.FromResult(leaseId == "lease-owner"); }
}
sealed class RecordingHandler : IIntegrationEventHandler
{
    public int Calls; public bool Fail;
    public Task Handle(IntegrationEnvelope message, CancellationToken ct) { Calls++; return Fail ? Task.FromException(new IOException("Injected handler failure")) : Task.CompletedTask; }
}
sealed class RecordingQueue : IQueueTransport
{
    public int Acks, Retries; public string LastReceipt = ""; public bool FailAck;
    public Task Publish(IntegrationEnvelope message, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<ReceivedEvent>> Receive(CancellationToken ct) => Task.FromResult<IReadOnlyList<ReceivedEvent>>([]);
    public Task Acknowledge(string receiptHandle, CancellationToken ct) { LastReceipt = receiptHandle; if (FailAck) throw new IOException("Injected ack failure"); Acks++; return Task.CompletedTask; }
    public Task Retry(string receiptHandle, int delaySeconds, CancellationToken ct) { Retries++; return Task.CompletedTask; }
}
