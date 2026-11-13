using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VortexBird.Infrastructure.Payments;

public static class MercadoPagoWebhookSignature
{
    // This payment-specific verifier requires all fields even though the generic provider
    // signature format permits absent optional fields for other notification topics.
    // A valid signature is evidence of notification origin, not proof of payment approval.
    public static bool Validate(string? signature, string? requestId, string? paymentId, string secret, DateTimeOffset now, TimeSpan? tolerance = null)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(signature) || signature.Length > 512 ||
            string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128 || requestId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
            string.IsNullOrWhiteSpace(paymentId) || paymentId.Length > 24 || paymentId.Any(c => c is < '0' or > '9')) return false;
        string? timestamp = null; string? digest = null;
        foreach (var part in signature.Split(','))
        {
            var pair = part.Trim().Split('=', 2);
            if (pair.Length != 2) return false;
            if (pair[0] == "ts") { if (timestamp is not null) return false; timestamp = pair[1]; }
            else if (pair[0] == "v1") { if (digest is not null) return false; digest = pair[1]; }
            else return false;
        }
        if (timestamp is null || timestamp.Length is not (10 or 13) || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var raw) || digest?.Length != 64) return false;
        DateTimeOffset sent;
        try { sent = timestamp.Length == 13 ? DateTimeOffset.FromUnixTimeMilliseconds(raw) : DateTimeOffset.FromUnixTimeSeconds(raw); }
        catch (ArgumentOutOfRangeException) { return false; }
        var window = tolerance ?? TimeSpan.FromMinutes(5);
        if (window <= TimeSpan.Zero || window > TimeSpan.FromMinutes(15) || (now - sent).Duration() > window) return false;
        byte[] actual;
        try { actual = Convert.FromHexString(digest); } catch (FormatException) { return false; }
        var manifest = $"id:{paymentId};request-id:{requestId};ts:{timestamp};";
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
