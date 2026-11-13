using VortexBird.Domain;

namespace VortexBird.Infrastructure.Payments;

public enum PaymentAction { Ignore, Confirm, Cancel, Refund, RecordRefund, ReviewChargeback }

/// <summary>Monotonic decisions shared by callback and polling, always evaluated under the order lock.</summary>
public static class PaymentOutcomePolicy
{
    public static PaymentAction Decide(string evidence, string orderStatus, string? acceptedId, string paymentId, string? previousStatus, bool validReservations)
    {
        if (previousStatus is "refunded" or "charged_back") return PaymentAction.Ignore;
        if (evidence == "refunded") return PaymentAction.RecordRefund;
        if (evidence == "charged_back") return PaymentAction.ReviewChargeback;
        if (evidence == "approved")
        {
            if (acceptedId == paymentId) return PaymentAction.Ignore;
            if (acceptedId is not null) return PaymentAction.Refund;
            return orderStatus is OrderStates.Reserved or OrderStates.AwaitingPayment && validReservations
                ? PaymentAction.Confirm : PaymentAction.Refund;
        }
        if (previousStatus == "approved" || acceptedId is not null) return PaymentAction.Ignore;
        return evidence is "rejected" or "cancelled" && orderStatus is OrderStates.Reserved or OrderStates.AwaitingPayment
            ? PaymentAction.Cancel : PaymentAction.Ignore;
    }
}
