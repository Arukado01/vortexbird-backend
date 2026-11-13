namespace VortexBird.Domain;

public static class OrderStates
{
    public const string Pending = "pending";
    public const string Reserved = "reserved";
    public const string AwaitingPayment = "awaiting_payment";
    public const string Confirmed = "confirmed";
    public const string Compensating = "compensating";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
    public const string Shipped = "shipped";
    public const string Delivered = "delivered";

    public static bool CanTransition(string current, string next) => (current, next) switch
    {
        (Pending, Reserved or Failed or Cancelled) => true,
        (Reserved, AwaitingPayment or Compensating) => true,
        (AwaitingPayment, Confirmed or Compensating) => true,
        (Compensating, Cancelled or Failed) => true,
        (Confirmed, Shipped or Compensating) => true,
        (Shipped, Delivered) => true,
        _ => false
    };
}

public static class PaymentStates
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Refunded = "refunded";
}

public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
