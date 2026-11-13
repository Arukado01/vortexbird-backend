using VortexBird.Application.Abstractions;
using VortexBird.Domain.Entities;

namespace VortexBird.Application.Contracts;

public record CreateOrderCommand(CreateOrderRequest Request, string Key) : ICommand<OrderReceipt>;
public record OrderReceipt(Guid Id, string Status, string PaymentStatus, decimal Total, string Currency, string CorrelationId, bool Replayed = false);
public record GetOrderQuery(Guid Id) : IQuery<Order?>;
