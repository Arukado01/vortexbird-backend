using VortexBird.Application.Contracts;

namespace VortexBird.Application.Abstractions;

public interface ICommand<out TResult> { }
public interface IQuery<out TResult> { }
public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<TResult> Handle(TCommand command, CancellationToken cancellationToken = default);
}
public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<TResult> Handle(TQuery query, CancellationToken cancellationToken = default);
}

public interface IClock { DateTimeOffset UtcNow { get; } }
public interface ICurrentActor
{
    string Subject { get; }
    Guid? PartnerId { get; }
    string CorrelationId { get; }
    Task<bool> HasPermission(string permission, CancellationToken cancellationToken = default);
    Task EnsurePartnerAccess(Guid partnerId, Guid? channelId = null, CancellationToken cancellationToken = default);
}
public interface IPaymentGateway
{
    string Provider { get; }
    Task<PaymentResult> Initiate(PaymentRequest request, CancellationToken cancellationToken = default);
    Task<PaymentResult> GetStatus(string reference, CancellationToken cancellationToken = default);
    Task Refund(string reference, string idempotencyKey, CancellationToken cancellationToken = default);
}
public interface IExternalSystemAdapter
{
    string System { get; }
    Task<AdapterHealth> CheckHealth(CancellationToken cancellationToken = default);
}
public interface IInventoryAdapter : IExternalSystemAdapter
{
    Task<StockResult> GetStock(string sku, string storeId, Guid partnerId, Guid channelId, CancellationToken cancellationToken = default);
    Task<string> Reserve(Guid orderId, string sku, string storeId, int quantity, CancellationToken cancellationToken = default);
    Task Release(string reservationReference, CancellationToken cancellationToken = default);
}
public interface IPricingAdapter : IExternalSystemAdapter
{
    Task<PriceResult> GetPrice(string sku, Guid partnerId, Guid channelId, string? customerId, CancellationToken cancellationToken = default);
}
