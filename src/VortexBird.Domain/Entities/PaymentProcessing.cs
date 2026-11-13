namespace VortexBird.Domain.Entities;

/// <summary>One durable, externally correlated checkout intent per order. Unknown creation is never POSTed again.</summary>
public sealed class PaymentCheckout
{
    public Guid OrderId { get; set; }
    public Guid PartnerId { get; set; }
    public Guid ChannelId { get; set; }
    public string State { get; set; } = "creating";
    public string ExternalReference { get; set; } = "";
    public decimal ExpectedAmount { get; set; }
    public string Currency { get; set; } = "COP";
    public string? PreferenceId { get; set; }
    public string? CheckoutUrl { get; set; }
    public string? AcceptedPaymentId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int Attempts { get; set; }
    public bool ReconcileRequested { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Minimal authenticated notification receipt; no card or customer data is persisted.</summary>
public sealed class PaymentInbox
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string NotificationKey { get; set; } = "";
    public string ProviderPaymentId { get; set; } = "";
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
}

/// <summary>Tracks each provider payment independently, including duplicate/late approvals requiring refunds.</summary>
public sealed class PaymentReceipt
{
    public string ProviderPaymentId { get; set; } = "";
    public Guid OrderId { get; set; }
    public string ProviderStatus { get; set; } = "pending";
    public string Disposition { get; set; } = "observed";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
