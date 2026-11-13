using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VortexBird.Domain.Entities;

namespace VortexBird.Infrastructure.Payments;

/// <summary>Durable leases and fenced completion permit several replicas and recovery after crashes.</summary>
public sealed class PaymentWorker(IServiceScopeFactory scopes, ILogger<PaymentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                for (var i = 0; i < 10 && await ProcessInbox(stoppingToken); i++) { }
                for (var i = 0; i < 10 && await ProcessCheckout(stoppingToken); i++) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Payment worker cycle failed ({ErrorType}); durable records remain retryable.", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task<bool> ProcessInbox(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var now = DateTimeOffset.UtcNow;
        var item = await db.Set<PaymentInbox>().AsNoTracking().Where(x => (x.Status == "pending" || x.Status == "processing") && x.NextAttemptAt <= now && (x.LeaseUntil == null || x.LeaseUntil < now))
            .OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (item is null) return false;
        var lease = Guid.NewGuid();
        var count = await db.Set<PaymentInbox>().Where(x => x.Id == item.Id && x.NextAttemptAt <= now && (x.Status == "pending" || x.Status == "processing") && (x.LeaseUntil == null || x.LeaseUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "processing").SetProperty(x => x.LeaseId, lease)
                .SetProperty(x => x.LeaseUntil, now.AddMinutes(2)).SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
        if (count == 0) return true;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(90));
            await scope.ServiceProvider.GetRequiredService<PaymentReconciler>().ProcessNotification(item.ProviderPaymentId, deadline.Token);
            await db.Set<PaymentInbox>().Where(x => x.Id == item.Id && x.LeaseId == lease).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, "processed").SetProperty(x => x.ProcessedAt, DateTimeOffset.UtcNow)
                .SetProperty(x => x.LeaseId, (Guid?)null).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null).SetProperty(x => x.LastError, (string?)null), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await db.Set<PaymentInbox>().Where(x => x.Id == item.Id && x.LeaseId == lease).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, item.Attempts + 1 >= 12 ? "dead_letter" : "pending")
                .SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(Math.Min(900, Math.Pow(2, item.Attempts + 1) * 5)))
                .SetProperty(x => x.LastError, ex.GetType().Name).SetProperty(x => x.LeaseId, (Guid?)null).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null), ct);
        }
        return true;
    }

    private async Task<bool> ProcessCheckout(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-30);
        var item = await db.Set<PaymentCheckout>().AsNoTracking().Where(x => (x.CreatedAt >= cutoff || x.ReconcileRequested) && x.Attempts < 12 && x.NextAttemptAt <= now && (x.LeaseUntil == null || x.LeaseUntil < now))
            .OrderBy(x => x.NextAttemptAt).FirstOrDefaultAsync(ct);
        if (item is null) return false;
        var lease = Guid.NewGuid();
        var count = await db.Set<PaymentCheckout>().Where(x => x.OrderId == item.OrderId && x.Attempts < 12 && x.NextAttemptAt <= now && (x.LeaseUntil == null || x.LeaseUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseId, lease).SetProperty(x => x.LeaseUntil, now.AddMinutes(3)), ct);
        if (count == 0) return true;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(2));
            await scope.ServiceProvider.GetRequiredService<PaymentReconciler>().ReconcileOrder(item.OrderId, lease, deadline.Token);
            await db.Set<PaymentCheckout>().Where(x => x.OrderId == item.OrderId && x.LeaseId == lease).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Attempts, 0).SetProperty(x => x.ReconcileRequested, false).SetProperty(x => x.LastError, (string?)null)
                .SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(item.ExpiresAt > now ? 30 : 300))
                .SetProperty(x => x.LeaseId, (Guid?)null).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await db.Set<PaymentCheckout>().Where(x => x.OrderId == item.OrderId && x.LeaseId == lease).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Attempts, x => x.Attempts + 1).SetProperty(x => x.LastError, ex.GetType().Name)
                .SetProperty(x => x.State, x => item.Attempts + 1 >= 12 ? "manual_review" : x.State)
                .SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(Math.Min(900, Math.Pow(2, item.Attempts + 1) * 5)))
                .SetProperty(x => x.LeaseId, (Guid?)null).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null), ct);
        }
        return true;
    }
}
