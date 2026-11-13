using System.Net;
using Microsoft.EntityFrameworkCore;
using VortexBird.Application.Abstractions;
using VortexBird.Application.Contracts;
using VortexBird.Domain.Entities;
using VortexBird.Infrastructure;

var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
void Reject(Action action, string message) { try { action(); } catch { checks++; Console.WriteLine("PASS " + message); return; } throw new Exception("Expected rejection: " + message); }
var partner = Guid.NewGuid(); var channel = Guid.NewGuid();
var one = new CreateOrderRequest(partner, channel, "BOG-01", "C1", [new("B", 1), new("A", 2)]);
var reordered = one with { Items = [new("A", 2), new("B", 1)] };
Check(RequestFingerprint.For(one) == RequestFingerprint.For(reordered), "Idempotency hash independent of line ordering");
Check(RequestFingerprint.For(one) != RequestFingerprint.For(one with { CustomerId = "C2" }), "Idempotency hash detects changed customer");
Check(RequestFingerprint.For(one) != RequestFingerprint.For(one with { PartnerId = Guid.NewGuid() }), "Idempotency hash binds tenant");
Check(!WebhookSafety.IsPublic(IPAddress.Parse("127.0.0.1")), "Reject loopback webhook");
Check(!WebhookSafety.IsPublic(IPAddress.Parse("169.254.169.254")), "Reject metadata webhook");
Check(!WebhookSafety.IsPublic(IPAddress.Parse("::ffff:10.0.0.1")), "Reject IPv4 mapped private webhook");
Reject(() => WebhookSafety.Validate("http://example.com"), "Reject HTTP webhook");
Reject(() => WebhookSafety.Validate("https://name:secret@example.com"), "Reject userinfo webhook");
Reject(() => AuthorizationScope.Resolve(partner, Guid.NewGuid().ToString(), []), "Reject conflicting partner claims");
Reject(() => AuthorizationScope.Resolve(null, "not-a-guid", []), "Reject malformed partner claims");
Check(AuthorizationScope.Resolve(null, partner.ToString(), [channel.ToString()]).PartnerId == partner, "Token scopes global DB actor down to tenant");
var options = new DbContextOptionsBuilder<IntegrationDbContext>().UseNpgsql("Host=localhost;Database=checks;Username=checks").Options;
using var db = new IntegrationDbContext(options);
var model = db.Model;
Check(model.FindEntityType(typeof(IdempotencyRecord))!.GetIndexes().Any(x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "PartnerId", "Key" })), "Idempotency has database tenant/key unique constraint");
Check(model.FindEntityType(typeof(Stock))!.FindProperty("Version")!.IsConcurrencyToken, "Stock optimistic concurrency token configured");
Check(model.FindEntityType(typeof(Stock))!.GetForeignKeys().Any(x => x.Properties.Select(p => p.Name).SequenceEqual(new[] { "ChannelId", "PartnerId" })), "Stock composite tenant/channel foreign key");
Check(model.FindEntityType(typeof(Order))!.GetForeignKeys().Any(x => x.Properties.Select(p => p.Name).SequenceEqual(new[] { "ChannelId", "PartnerId" })), "Order composite tenant/channel foreign key");
var audit = new ActivityLog(); db.Attach(audit); db.Entry(audit).State = EntityState.Deleted;
Reject(() => db.SaveChanges(), "Synchronous audit delete prohibited before database access");
await CheckAsync();
async Task CheckAsync() { try { await db.SaveChangesAsync(); } catch (InvalidOperationException ex) when (ex.Message.Contains("append-only")) { checks++; Console.WriteLine("PASS Async audit mutation prohibited"); return; } throw new Exception("Audit mutation allowed"); }
Check(typeof(Order).Assembly.GetReferencedAssemblies().All(x => !x.Name!.Contains("Infrastructure") && !x.Name.Contains("EntityFramework")), "Domain has no infrastructure dependency");
Check(typeof(ICurrentActor).Assembly.GetReferencedAssemblies().All(x => !x.Name!.Contains("Infrastructure") && !x.Name.Contains("EntityFramework")), "Application has no infrastructure dependency");
Console.WriteLine($"{checks}/{checks} invariant and architecture checks passed. PostgreSQL integration tests require a running database.");
