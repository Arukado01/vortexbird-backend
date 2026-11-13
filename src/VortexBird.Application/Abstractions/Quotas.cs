namespace VortexBird.Application.Abstractions;

public sealed record QuotaBudget(string Key, int Limit);
public sealed record ChannelQuota(Guid ChannelId, int RequestsPerMinute);
public sealed record QuotaDecision(bool Allowed, int RetryAfterSeconds);
public interface IQuotaStore
{
    Task<QuotaDecision> TryAcquire(Guid partnerId, IReadOnlyList<QuotaBudget> budgets, CancellationToken ct = default);
}

public static class QuotaPlanner
{
    // visibleChannels is already intersected with the authenticated actor's scope.
    // Aggregate reads spend all visible channel budgets; an explicit target spends only its own.
    public static IReadOnlyList<QuotaBudget> Plan(Guid partnerId, int partnerLimit, IReadOnlyCollection<ChannelQuota> visibleChannels, Guid? targetChannel)
    {
        if (partnerId == Guid.Empty || partnerLimit is < 10 or > 100000)
            throw new ArgumentException("Invalid partner quota configuration.");
        if (visibleChannels.Any(x => x.ChannelId == Guid.Empty || x.RequestsPerMinute is < 10 or > 100000)
            || visibleChannels.Select(x => x.ChannelId).Distinct().Count() != visibleChannels.Count)
            throw new ArgumentException("Invalid or duplicate channel quota configuration.");
        if (targetChannel.HasValue && visibleChannels.All(x => x.ChannelId != targetChannel.Value))
            throw new ArgumentException("The target channel is outside the validated quota scope.");
        List<QuotaBudget> budgets = [new("partner", partnerLimit)];
        budgets.AddRange(visibleChannels.Where(x => !targetChannel.HasValue || x.ChannelId == targetChannel.Value)
            .OrderBy(x => x.ChannelId).Select(x => new QuotaBudget($"channel:{x.ChannelId:N}", x.RequestsPerMinute)));
        return budgets;
    }
}
