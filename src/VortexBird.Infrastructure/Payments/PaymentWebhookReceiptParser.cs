using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VortexBird.Infrastructure.Payments;

public sealed record AuthenticatedPaymentNotification(string PaymentId, string NotificationKey);

public static class PaymentWebhookReceiptParser
{
    public static AuthenticatedPaymentNotification? Parse(string body, string signature, string requestId, string queryPaymentId, string secret, long expectedSeller, DateTimeOffset now)
    {
        if (!MercadoPagoWebhookSignature.Validate(signature, requestId, queryPaymentId, secret, now)) return null;
        if (Encoding.UTF8.GetByteCount(body) > 32768) return null;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            var json = document.RootElement;
            if (!json.TryGetProperty("type", out var type) || type.GetString() != "payment"
                || !json.TryGetProperty("live_mode", out var live) || live.ValueKind != JsonValueKind.False
                || !json.TryGetProperty("user_id", out var seller) || !long.TryParse(seller.ToString(), out var id) || id != expectedSeller
                || !json.TryGetProperty("data", out var data) || !data.TryGetProperty("id", out var payment) || payment.ToString() != queryPaymentId)
                return null;
            // The body notification ID/status are NOT signed evidence; use only the authenticated manifest for dedupe.
            var timestamp = signature.Split(',').Select(part => part.Trim().Split('=', 2)).Single(part => part[0].Trim() == "ts")[1].Trim();
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"id:{queryPaymentId};request-id:{requestId};ts:{timestamp};")));
            return new(queryPaymentId, key);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }
}
