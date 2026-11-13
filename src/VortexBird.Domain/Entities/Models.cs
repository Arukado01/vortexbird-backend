namespace VortexBird.Domain.Entities;

public class Partner
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int RequestsPerMinute { get; set; } = 120;
}

public class Channel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PartnerId { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int RequestsPerMinute { get; set; } = 120;
}

public class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public bool Active { get; set; } = true;
}

public class Stock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PartnerId { get; set; }
    public Guid ChannelId { get; set; }
    public string StoreId { get; set; } = "";
    public string Sku { get; set; } = "";
    public int OnHand { get; set; }
    public int Reserved { get; set; }
    public long Version { get; set; }
    public int Available => OnHand - Reserved;

    public void Reserve(int quantity)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (quantity > Available) throw new DomainException("insufficient_stock", "La cantidad solicitada supera el inventario disponible.");
        Reserved += quantity;
        Version++;
    }

    public void Release(int quantity)
    {
        if (quantity <= 0 || quantity > Reserved) throw new ArgumentOutOfRangeException(nameof(quantity));
        Reserved -= quantity;
        Version++;
    }

    public void Commit(int quantity)
    {
        if (quantity <= 0 || quantity > Reserved) throw new ArgumentOutOfRangeException(nameof(quantity));
        Reserved -= quantity;
        OnHand -= quantity;
        Version++;
    }
}

public class Price
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PartnerId { get; set; }
    public Guid ChannelId { get; set; }
    public string Sku { get; set; } = "";
    public string? CustomerId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "COP";
}

public class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PartnerId { get; set; }
    public Guid ChannelId { get; set; }
    public string StoreId { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string Status { get; set; } = OrderStates.Pending;
    public string PaymentStatus { get; set; } = PaymentStates.Pending;
    public decimal Total { get; set; }
    public string Currency { get; set; } = "COP";
    public string CorrelationId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<OrderItem> Items { get; set; } = [];
    public List<OrderHistory> Timeline { get; set; } = [];

    public void TransitionTo(string nextStatus, string description, DateTimeOffset at)
    {
        if (!OrderStates.CanTransition(Status, nextStatus))
            throw new DomainException("invalid_order_transition", $"No se permite cambiar una orden de {Status} a {nextStatus}.");
        Status = nextStatus;
        UpdatedAt = at;
        Timeline.Add(new OrderHistory { OrderId = Id, Status = nextStatus, Description = description, CreatedAt = at });
    }
}

public class OrderItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public class OrderHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string Status { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class InventoryReservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PartnerId { get; set; }
    public Guid OrderId { get; set; }
    public Guid StockId { get; set; }
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
    public string Status { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}

public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Guid PartnerId { get; set; }
    public string Provider { get; set; } = "simulator";
    public string Reference { get; set; } = "";
    public decimal Amount { get; set; }
    public string Status { get; set; } = PaymentStates.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class IdempotencyRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PartnerId { get; set; }
    public string Key { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string State { get; set; } = "processing";
    public Guid? OrderId { get; set; }
    public string ResponseJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}

public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PartnerId { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? LastError { get; set; }
    public string CorrelationId { get; set; } = "";
    public int Version { get; set; } = 1;
    public string LeaseId { get; set; } = "";
}

public class ProcessedMessage
{
    public Guid Id { get; set; }
    public string Consumer { get; set; } = "";
    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ActivityLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "system";
    public Guid? PartnerId { get; set; }
    public Guid? ChannelId { get; set; }
    public string Action { get; set; } = "";
    public string Module { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Result { get; set; } = "success";
    public string Description { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Subject { get; set; } = "";
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public Guid? PartnerId { get; set; }
}

public class Role
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Protected { get; set; }
}

public class Permission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Module { get; set; } = "";
}

public class UserRole
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
}

public class RolePermission
{
    public Guid RoleId { get; set; }
    public Guid PermissionId { get; set; }
}

public class Integration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string System { get; set; } = "";
    public string Mode { get; set; } = "simulator";
    public string Status { get; set; } = "unknown";
    public string FailureMode { get; set; } = "none";
    public double LatencyMs { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
}

public class IntegrationExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IntegrationId { get; set; }
    public Guid? PartnerId { get; set; }
    public string Operation { get; set; } = "";
    public string Status { get; set; } = "success";
    public double DurationMs { get; set; }
    public string? Error { get; set; }
    public string CorrelationId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class WebhookSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PartnerId { get; set; }
    public string Url { get; set; } = "";
    public string EventType { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string SigningSecret { get; set; } = "";
}

public class WebhookDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubscriptionId { get; set; }
    public Guid EventId { get; set; }
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public int? StatusCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LastError { get; set; }
}

public class Customer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PartnerId { get; set; }
    public string ExternalId { get; set; } = "";
    public string DisplayName { get; set; } = "";
}
