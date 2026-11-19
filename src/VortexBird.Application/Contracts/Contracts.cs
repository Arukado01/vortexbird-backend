namespace VortexBird.Application.Contracts;

public record OrderLineRequest(string Sku, int Quantity);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public record CreateOrderRequest(Guid PartnerId, Guid ChannelId, string StoreId, string CustomerId, List<OrderLineRequest> Items);
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;
}

public record PageRequest(int Page = 1, int PageSize = 20, string? Search = null, string? SortBy = null, string SortDirection = "desc")
{
    public int SafePage => Math.Max(1, Page);
    public int SafePageSize => Math.Clamp(PageSize, 1, 100);
    public int Offset => checked((SafePage - 1) * SafePageSize);
}

public record StockResult(string Sku, string StoreId, Guid ChannelId, int OnHand, int Reserved, int Available, DateTimeOffset AsOf, bool Stale = false);
public record PriceResult(string Sku, Guid ChannelId, string? CustomerId, decimal Amount, string Currency, DateTimeOffset AsOf);
public record PaymentRequest(Guid OrderId, Guid PartnerId, decimal Amount, string Currency, string IdempotencyKey);
public record PaymentResult(string Reference, string Status, string Provider, bool Simulated);
public record AdapterHealth(string System, bool Available, double LatencyMs, string Mode, string? Error);
public record UserContext(string Subject, Guid? PartnerId, IReadOnlySet<Guid> ChannelIds, IReadOnlySet<string> Permissions);
public record ApiError(string Type, string Title, int Status, string Detail, string Instance, string ErrorCode, string CorrelationId, DateTimeOffset Timestamp, IDictionary<string, string[]>? ValidationErrors = null);
