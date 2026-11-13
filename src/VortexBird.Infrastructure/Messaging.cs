using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VortexBird.Domain.Entities;
using VortexBird.Application.Abstractions;

namespace VortexBird.Infrastructure;

public static class WebhookSafety
{
    public static Uri Validate(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || uri.Port != 443 || uri.HostNameType != UriHostNameType.Dns)
            throw new ArgumentException("El webhook requiere HTTPS, dominio público y puerto 443.");
        return uri;
    }
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return (b[0] & 0xe0) == 0x20; // Global unicast only.
        return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] == 198 && b[1] is 18 or 19);
    }
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            if (addresses.Length == 0 || addresses.Any(a => !IsPublic(a))) throw new HttpRequestException("Destino webhook no permitido.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(addresses[0], context.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        }
    }) { Timeout = TimeSpan.FromSeconds(5) };
}

public sealed class OutboxWorker(IServiceScopeFactory factory, ILogger<OutboxWorker> logger) : BackgroundService
{
    private readonly string workerId = Guid.NewGuid().ToString("N");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatch(stoppingToken);
                await ExpireReservations(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning(ex, "Outbox iteration unavailable; durable messages retained"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
    private async Task ProcessBatch(CancellationToken ct)
    {
        using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var now = DateTimeOffset.UtcNow;
        // Lock only claim transaction; release before external delivery. Lease recovers a crashed worker.
        List<OutboxMessage> messages;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            messages = await db.OutboxMessages.FromSqlInterpolated($"SELECT * FROM \"OutboxMessages\" WHERE (\"Status\"='pending' OR (\"Status\"='processing' AND \"NextAttemptAt\" < {now})) AND \"NextAttemptAt\" <= {now} ORDER BY \"CreatedAt\" LIMIT 10 FOR UPDATE SKIP LOCKED").ToListAsync(ct);
            foreach (var m in messages) { m.Status = "processing"; m.LeaseId = Guid.NewGuid().ToString("N"); m.NextAttemptAt = now.AddMinutes(2); }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        foreach (var message in messages)
        {
            try
            {
                using var work = factory.CreateScope();
                var workDb = work.ServiceProvider.GetRequiredService<IntegrationDbContext>();
                if (!await workDb.ProcessedMessages.AnyAsync(x => x.Id == message.Id && x.Consumer == "integration-router-v1", ct))
                {
                    if (message.Type == "order.created")
                    {
                        using var payload = JsonDocument.Parse(message.Payload);
                        await work.ServiceProvider.GetRequiredService<OrderSaga>().Advance(payload.RootElement.GetProperty("orderId").GetGuid(), payload.RootElement.GetProperty("paymentOutcome").GetString()!.ToLowerInvariant(), ct);
                    }
                    if (message.Type is "order.confirmed" or "shipment.started" or "delivery.confirmed")
                    {
                        using var payload = JsonDocument.Parse(message.Payload);
                        await work.ServiceProvider.GetRequiredService<IOrderManagementAdapter>().SynchronizeOrder(payload.RootElement.GetProperty("orderId").GetGuid(), ct);
                    }
                    await Deliver(workDb, message, ct);
                    workDb.ProcessedMessages.Add(new ProcessedMessage { Id = message.Id, Consumer = "integration-router-v1" });
                    await workDb.SaveChangesAsync(ct);
                }
                message.Status = "processed"; message.ProcessedAt = DateTimeOffset.UtcNow; message.LastError = null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                message.Attempts++;
                message.Status = message.Attempts >= 5 ? "dead_letter" : "pending";
                message.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, message.Attempts) + Random.Shared.NextDouble());
                message.LastError = ex.GetType().Name + ": delivery failed; see correlated logs";
                logger.LogWarning(ex, "Event {EventId} {CorrelationId} attempt {Attempt} worker {Worker}", message.Id, message.CorrelationId, message.Attempts, workerId);
            }
            var lease = message.LeaseId;
            db.Entry(message).State = EntityState.Detached;
            // Fencing prevents a stale worker from overwriting the state of a re-leased delivery.
            await db.OutboxMessages.Where(x => x.Id == message.Id && x.LeaseId == lease && x.Status == "processing").ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, message.Status).SetProperty(x => x.Attempts, message.Attempts)
                .SetProperty(x => x.NextAttemptAt, message.NextAttemptAt).SetProperty(x => x.ProcessedAt, message.ProcessedAt)
                .SetProperty(x => x.LastError, message.LastError), ct);
        }
    }
    private static async Task Deliver(IntegrationDbContext db, OutboxMessage message, CancellationToken ct)
    {
        foreach (var subscription in await db.WebhookSubscriptions.Where(x => x.PartnerId == message.PartnerId && x.EventType == message.Type && x.Enabled).ToListAsync(ct))
        {
            var delivery = await db.WebhookDeliveries.SingleOrDefaultAsync(x => x.SubscriptionId == subscription.Id && x.EventId == message.Id, ct);
            if (delivery?.Status == "delivered") continue;
            if (delivery is null) { delivery = new WebhookDelivery { SubscriptionId = subscription.Id, EventId = message.Id }; db.WebhookDeliveries.Add(delivery); }
            delivery.Attempts++;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var envelope = JsonSerializer.Serialize(new { id = message.Id, type = message.Type, version = message.Version, occurredAt = message.CreatedAt, correlationId = message.CorrelationId, data = JsonSerializer.Deserialize<JsonElement>(message.Payload) });
            var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(subscription.SigningSecret), Encoding.UTF8.GetBytes(timestamp + "." + envelope))).ToLowerInvariant();
            using var request = new HttpRequestMessage(HttpMethod.Post, WebhookSafety.Validate(subscription.Url)) { Content = new StringContent(envelope, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-VortexBird-Signature", $"t={timestamp},v1={signature}");
            request.Headers.Add("X-Event-Id", message.Id.ToString());
            request.Headers.Add("X-Correlation-Id", message.CorrelationId);
            using var client = WebhookSafety.CreateClient();
            try
            {
                using var response = await client.SendAsync(request, ct);
                delivery.StatusCode = (int)response.StatusCode;
                delivery.Status = response.IsSuccessStatusCode ? "delivered" : "failed";
                await db.SaveChangesAsync(ct);
                response.EnsureSuccessStatusCode();
            }
            catch { delivery.Status = "failed"; delivery.LastError = "Delivery failed"; await db.SaveChangesAsync(ct); throw; }
        }
    }
    private async Task ExpireReservations(CancellationToken ct)
    {
        using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var ids = await db.InventoryReservations.Where(x => x.Status == "active" && x.ExpiresAt < DateTimeOffset.UtcNow).Select(x => x.OrderId).Distinct().Take(20).ToListAsync(ct);
        foreach (var id in ids) await scope.ServiceProvider.GetRequiredService<OrderSaga>().Cancel(id, ct);
    }
}
