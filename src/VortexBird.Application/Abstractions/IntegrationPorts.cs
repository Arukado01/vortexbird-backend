namespace VortexBird.Application.Abstractions;

public interface ICustomerAdapter { Task ValidateCustomer(string externalId, Guid partnerId, CancellationToken ct); }
public interface IOrderManagementAdapter { Task SynchronizeOrder(Guid orderId, CancellationToken ct); }
