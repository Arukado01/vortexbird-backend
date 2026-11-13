using Microsoft.EntityFrameworkCore;
using VortexBird.Application.Abstractions;
using VortexBird.Domain.Entities;

namespace VortexBird.Infrastructure;

public sealed class PostgresQuotaStore(IntegrationDbContext db) : IQuotaStore
{
    public async Task<QuotaDecision> TryAcquire(Guid partnerId, IReadOnlyList<QuotaBudget> budgets, CancellationToken ct = default)
    {
        if (partnerId == Guid.Empty || budgets.Count == 0 || budgets.Count(x => x.Key == "partner") != 1
            || budgets.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != budgets.Count
            || budgets.Any(x => x.Limit is < 10 or > 100000 || !IsBudgetKey(x.Key)))
            throw new ArgumentException("Invalid quota budget plan.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // All API replicas share this transaction-scoped tenant lock. Hash collisions
        // only serialize unrelated tenants; they cannot merge counters or grant permits.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({$"quota:{partnerId:N}"}, 0))", ct);
        var now = await db.Database.SqlQuery<long>($"SELECT floor(extract(epoch FROM clock_timestamp()))::bigint AS \"Value\"").SingleAsync(ct);
        var minute = now / 60;
        var retryAfter = (int)(60 - now % 60);
        var keys = budgets.Select(x => x.Key).ToArray();
        var current = await db.Set<QuotaCounter>().AsNoTracking().Where(x => x.PartnerId == partnerId && keys.Contains(x.BudgetKey)).ToDictionaryAsync(x => x.BudgetKey, ct);
        if (budgets.Any(b => current.TryGetValue(b.Key, out var counter) && counter.Minute == minute && counter.Count >= b.Limit))
        {
            await tx.RollbackAsync(ct);
            return new QuotaDecision(false, retryAfter);
        }
        // Check every budget before spending any. The lock makes check+upsert atomic.
        foreach (var budget in budgets)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "QuotaCounters" ("PartnerId", "BudgetKey", "Minute", "Count") VALUES ({partnerId}, {budget.Key}, {minute}, 1)
                ON CONFLICT ("PartnerId", "BudgetKey") DO UPDATE SET
                    "Count" = CASE WHEN "QuotaCounters"."Minute" = EXCLUDED."Minute" THEN "QuotaCounters"."Count" + 1 ELSE 1 END,
                    "Minute" = EXCLUDED."Minute"
                """, ct);
        await tx.CommitAsync(ct);
        return new QuotaDecision(true, 0);
        // Database failures deliberately propagate: requests never get unmetered fallback.
    }

    private static bool IsBudgetKey(string key) => key == "partner" || key.StartsWith("channel:", StringComparison.Ordinal)
        && Guid.TryParseExact(key[8..], "N", out var channel) && channel != Guid.Empty && key == $"channel:{channel:N}";
}
