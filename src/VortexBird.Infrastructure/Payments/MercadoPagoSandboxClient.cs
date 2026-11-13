using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace VortexBird.Infrastructure.Payments;

public sealed record MercadoPagoSandboxOptions(bool Enabled, string AccessToken, long ExpectedTestSellerId, Uri NotificationUrl, Uri ReturnUrl);
public sealed record SandboxCheckoutRequest(Guid OrderId, Guid PartnerId, decimal Amount, string Currency = "COP");
public sealed record SandboxCheckout(string PreferenceId, Uri CheckoutUrl, string ExternalReference, bool TestSellerVerified);
public sealed record VerifiedSandboxPayment(string PaymentId, string Status, decimal Amount, string Currency, string ExternalReference);

/// <summary>
/// Isolated Checkout Pro Preferences pilot. Intentionally NOT registered as the application's
/// payment gateway until durable checkout persistence and reconciliation are integrated.
/// This client never receives PAN, CVV or card tokens, and never retries a preference POST.
/// </summary>
public sealed class MercadoPagoSandboxClient(HttpClient http, MercadoPagoSandboxOptions options)
{
    private static readonly Uri Api = new("https://api.mercadopago.com/");
    private static readonly Uri SellerIdentity = new("https://api.mercadolibre.com/users/me");

    public async Task<SandboxCheckout> CreateCheckout(SandboxCheckoutRequest input, CancellationToken ct = default)
    {
        ValidateInput(input);
        ValidateConfiguration();
        // APP_USR credentials can belong to either real or test sellers. The prefix proves nothing.
        await VerifyTestSeller(ct);
        var reference = Reference(input);
        var payload = new
        {
            items = new[] { new { id = input.OrderId.ToString("N"), title = "AndesRetail - pedido de prueba", quantity = 1, currency_id = input.Currency, unit_price = input.Amount } },
            external_reference = reference,
            notification_url = options.NotificationUrl.AbsoluteUri,
            back_urls = new { success = options.ReturnUrl.AbsoluteUri, pending = options.ReturnUrl.AbsoluteUri, failure = options.ReturnUrl.AbsoluteUri },
            metadata = new { order_id = input.OrderId.ToString("N"), partner_id = input.PartnerId.ToString("N"), environment = "sandbox" }
        };
        using var request = Authorized(HttpMethod.Post, new Uri(Api, "checkout/preferences"));
        request.Content = JsonContent.Create(payload);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await RequireSuccess(response, ct);
        using var document = await ReadBounded(response, ct);
        var json = document.RootElement;
        if (!json.TryGetProperty("collector_id", out var seller) || seller.GetInt64() != options.ExpectedTestSellerId)
            throw new InvalidOperationException("La preferencia no pertenece al vendedor de prueba configurado.");
        var id = RequiredString(json, "id");
        var checkout = new Uri(RequiredString(json, "init_point"), UriKind.Absolute);
        if (checkout.Scheme != "https" || checkout.UserInfo.Length != 0 || checkout.Port != 443 ||
            checkout.Host is not ("www.mercadopago.com.co" or "www.mercadopago.com" or "sandbox.mercadopago.com"))
            throw new InvalidOperationException("El proveedor devolvió un destino de checkout no permitido.");
        return new SandboxCheckout(id, checkout, reference, true);
    }

    public async Task<VerifiedSandboxPayment> ReadVerifiedPayment(string paymentId, SandboxCheckoutRequest expected, CancellationToken ct = default)
    {
        ValidateInput(expected);
        ValidateConfiguration();
        if (string.IsNullOrWhiteSpace(paymentId) || paymentId.Length > 24 || paymentId.Any(c => c is < '0' or > '9'))
            throw new ArgumentException("El identificador de pago debe ser numérico.", nameof(paymentId));
        await VerifyTestSeller(ct);
        using var request = Authorized(HttpMethod.Get, new Uri(Api, "v1/payments/" + paymentId));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await RequireSuccess(response, ct);
        using var document = await ReadBounded(response, ct);
        var json = document.RootElement;
        if (!json.TryGetProperty("live_mode", out var live) || live.ValueKind != JsonValueKind.False)
            throw new InvalidOperationException("Se rechazó un pago productivo o sin evidencia explícita de modo de pruebas.");
        if (!json.TryGetProperty("collector_id", out var seller) || seller.GetInt64() != options.ExpectedTestSellerId)
            throw new InvalidOperationException("El pago pertenece a otro vendedor.");
        var returnedId = json.GetProperty("id").ToString();
        var amount = json.GetProperty("transaction_amount").GetDecimal();
        var currency = RequiredString(json, "currency_id");
        var reference = RequiredString(json, "external_reference");
        if (returnedId != paymentId || amount != expected.Amount || currency != expected.Currency || reference != Reference(expected))
            throw new InvalidOperationException("El pago no coincide con la orden, partner, importe y moneda esperados.");
        var status = RequiredString(json, "status");
        if (status is not ("pending" or "approved" or "rejected" or "cancelled" or "in_process" or "refunded" or "charged_back" or "authorized"))
            throw new InvalidOperationException("Estado de pago desconocido; requiere conciliación.");
        return new VerifiedSandboxPayment(paymentId, status, amount, currency, reference);
    }

    private async Task VerifyTestSeller(CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get, SellerIdentity);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await RequireSuccess(response, ct);
        using var document = await ReadBounded(response, ct);
        var json = document.RootElement;
        if (!json.TryGetProperty("id", out var id) || id.GetInt64() != options.ExpectedTestSellerId ||
            !json.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array ||
            !tags.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "test_user"))
            throw new InvalidOperationException("La credencial no corresponde al vendedor de prueba verificado. No se iniciará un checkout.");
    }

    private HttpRequestMessage Authorized(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
    private void ValidateConfiguration()
    {
        if (!options.Enabled) throw new InvalidOperationException("El adaptador sandbox no está habilitado.");
        if (string.IsNullOrWhiteSpace(options.AccessToken) || options.AccessToken.Any(char.IsWhiteSpace) || options.ExpectedTestSellerId <= 0)
            throw new InvalidOperationException("Faltan credenciales y vendedor de prueba explícitos.");
        foreach (var url in new[] { options.NotificationUrl, options.ReturnUrl })
            if (!url.IsAbsoluteUri || url.Scheme != "https" || url.UserInfo.Length != 0 || url.Port != 443 || url.IsLoopback || url.Fragment.Length != 0)
                throw new InvalidOperationException("Las URLs de retorno y notificación requieren HTTPS público sin credenciales ni fragmento.");
    }
    private static void ValidateInput(SandboxCheckoutRequest input)
    {
        if (input.OrderId == Guid.Empty || input.PartnerId == Guid.Empty || input.Amount <= 0 || input.Amount > 10_000_000m || decimal.Round(input.Amount, 2) != input.Amount || input.Currency != "COP")
            throw new ArgumentException("Orden/partner, importe o moneda de prueba inválidos.", nameof(input));
    }
    private static string Reference(SandboxCheckoutRequest input) => $"{input.PartnerId:N}:{input.OrderId:N}";
    private static string RequiredString(JsonElement json, string key) => json.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
        ? value.GetString()! : throw new InvalidOperationException("Respuesta del proveedor incompleta.");
    private static Task RequireSuccess(HttpResponseMessage response, CancellationToken _) => response.IsSuccessStatusCode
        ? Task.CompletedTask : throw new HttpRequestException("El proveedor rechazó la solicitud; revisar la referencia sin registrar secretos ni el cuerpo de error.", null, response.StatusCode);
    private static async Task<JsonDocument> ReadBounded(HttpResponseMessage response, CancellationToken ct)
    {
        const int max = 128 * 1024;
        if (response.Content.Headers.ContentLength > max) throw new InvalidOperationException("Respuesta del proveedor demasiado grande.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > max) throw new InvalidOperationException("Respuesta del proveedor demasiado grande.");
            buffer.Write(chunk, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }
}
