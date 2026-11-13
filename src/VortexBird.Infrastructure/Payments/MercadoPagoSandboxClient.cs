using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace VortexBird.Infrastructure.Payments;

public sealed record MercadoPagoSandboxOptions(bool Enabled, string AccessToken, long ExpectedTestSellerId, Uri NotificationUrl, Uri ReturnUrl);
public sealed record SandboxCheckoutRequest(Guid OrderId, Guid PartnerId, decimal Amount, string Currency = "COP", DateTimeOffset? ExpiresAt = null);
public sealed record SandboxCheckout(string PreferenceId, Uri CheckoutUrl, string ExternalReference, bool TestSellerVerified);
public sealed record VerifiedSandboxPayment(string PaymentId, string Status, decimal Amount, string Currency, string ExternalReference);

/// <summary>
/// Checkout Pro Preferences sandbox client. Durable workflow decisions belong to the caller.
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
        var now = DateTimeOffset.UtcNow;
        var expiresAt = input.ExpiresAt ?? now.AddMinutes(15);
        if (expiresAt <= now || expiresAt > now.AddMinutes(15))
            throw new ArgumentException("El checkout requiere un vencimiento futuro de hasta 15 minutos.", nameof(input));
        // APP_USR credentials can belong to either real or test sellers. The prefix proves nothing.
        await VerifyTestSeller(ct);
        var reference = Reference(input);
        var payload = new
        {
            items = new[] { new { id = input.OrderId.ToString("N"), title = "AndesRetail - pedido de prueba", quantity = 1, currency_id = input.Currency, unit_price = input.Amount } },
            external_reference = reference,
            expires = true,
            expiration_date_from = now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            expiration_date_to = expiresAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            notification_url = options.NotificationUrl.AbsoluteUri,
            back_urls = new { success = options.ReturnUrl.AbsoluteUri, pending = options.ReturnUrl.AbsoluteUri, failure = options.ReturnUrl.AbsoluteUri },
            metadata = new { order_id = input.OrderId.ToString("N"), partner_id = input.PartnerId.ToString("N"), environment = "sandbox" }
        };
        using var request = Authorized(HttpMethod.Post, new Uri(Api, "checkout/preferences"));
        request.Content = JsonContent.Create(payload);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await RequireSuccess(response, ct);
        using var document = await ReadBounded(response, ct);
        return ReadCheckout(document.RootElement, input, requireFullMatch: false);
    }

    public async Task<VerifiedSandboxPayment> ReadVerifiedPayment(string paymentId, SandboxCheckoutRequest expected, CancellationToken ct = default)
    {
        ValidateInput(expected);
        var payment = await ReadSandboxPayment(paymentId, ct);
        AssertMatches(payment, expected);
        return payment;
    }

    public async Task<VerifiedSandboxPayment> ReadSandboxPayment(string paymentId, CancellationToken ct = default)
    {
        ValidateConfiguration();
        ValidatePaymentId(paymentId);
        await VerifyTestSeller(ct);
        return await ReadPayment(paymentId, ct);
    }

    public static void AssertMatches(VerifiedSandboxPayment payment, SandboxCheckoutRequest expected)
    {
        ValidateInput(expected);
        if (payment.Amount != expected.Amount || payment.Currency != expected.Currency || payment.ExternalReference != Reference(expected))
            throw new InvalidOperationException("El pago no coincide con la orden, partner, importe y moneda esperados.");
    }

    /// <summary>Recovers a single existing preference; never retries an ambiguous creation POST.</summary>
    public async Task<SandboxCheckout?> SearchCheckout(SandboxCheckoutRequest expected, CancellationToken ct = default)
    {
        ValidateInput(expected);
        ValidateConfiguration();
        await VerifyTestSeller(ct);
        using var request = Authorized(HttpMethod.Get, new Uri(Api, "checkout/preferences/search?external_reference=" + Uri.EscapeDataString(Reference(expected)) + "&limit=2"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await RequireSuccess(response, ct);
        using var document = await ReadBounded(response, ct);
        var root = document.RootElement;
        if (!root.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("total", out var total) || !total.TryGetInt32(out var count) || count < 0 ||
            count != elements.GetArrayLength() || count > 1)
            throw new InvalidOperationException("La búsqueda de preferencias es incompleta o ambigua; requiere conciliación.");
        if (count == 0) return null;
        var id = RequiredString(elements[0], "id");
        ValidatePreferenceId(id);
        using var get = Authorized(HttpMethod.Get, new Uri(Api, "checkout/preferences/" + id));
        using var detail = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
        await RequireSuccess(detail, ct);
        using var preference = await ReadBounded(detail, ct);
        var checkout = ReadCheckout(preference.RootElement, expected, requireFullMatch: true);
        if (checkout.PreferenceId != id) throw new InvalidOperationException("La preferencia recuperada cambió de identificador.");
        return checkout;
    }

    /// <summary>Returns only individually verified payments. Multiple attempts remain visible to the durable reconciler.</summary>
    public async Task<IReadOnlyList<VerifiedSandboxPayment>> SearchPayments(SandboxCheckoutRequest expected, CancellationToken ct = default)
    {
        ValidateInput(expected);
        ValidateConfiguration();
        await VerifyTestSeller(ct);
        using var request = Authorized(HttpMethod.Get, new Uri(Api, "v1/payments/search?external_reference=" + Uri.EscapeDataString(Reference(expected)) + "&limit=50&offset=0&sort=date_created&criteria=asc"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await RequireSuccess(response, ct);
        using var document = await ReadBounded(response, ct);
        var root = document.RootElement;
        // Some API reference examples wrap the single search envelope in an array.
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1) root = root[0];
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("paging", out var paging) || paging.ValueKind != JsonValueKind.Object ||
            !paging.TryGetProperty("total", out var total) || !total.TryGetInt32(out var count) || count < 0 || count > 50 ||
            !paging.TryGetProperty("offset", out var offset) || !offset.TryGetInt32(out var offsetValue) || offsetValue != 0 ||
            results.GetArrayLength() != count)
            throw new InvalidOperationException("La búsqueda de pagos excede el límite o está incompleta; requiere conciliación manual.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in results.EnumerateArray())
        {
            var id = PaymentId(item);
            ValidatePaymentId(id);
            if (!ids.Add(id)) throw new InvalidOperationException("La búsqueda de pagos contiene identificadores duplicados.");
        }
        var payments = new List<VerifiedSandboxPayment>();
        foreach (var id in ids)
        {
            var payment = await ReadPayment(id, ct);
            AssertMatches(payment, expected);
            payments.Add(payment);
        }
        return payments.OrderBy(x => x.Status == "approved" ? 0 : 1).ThenBy(x => x.PaymentId.Length).ThenBy(x => x.PaymentId, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The POST response is not proof of refund. Only the authoritative payment reread may report refunded.</summary>
    public async Task<VerifiedSandboxPayment> RefundVerifiedPayment(string paymentId, SandboxCheckoutRequest expected, string stableKey, CancellationToken ct = default)
    {
        ValidateInput(expected);
        ValidateConfiguration();
        ValidatePaymentId(paymentId);
        if (string.IsNullOrWhiteSpace(stableKey) || stableKey.Length > 128 || stableKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or ':' or '.')))
            throw new ArgumentException("Se requiere una clave de reembolso estable y válida.", nameof(stableKey));
        await VerifyTestSeller(ct);
        var before = await ReadPayment(paymentId, ct);
        AssertMatches(before, expected);
        if (before.Status == "refunded") return before;
        if (before.Status != "approved") throw new InvalidOperationException("Solo se reembolsan pagos aprobados y verificados.");
        using var request = Authorized(HttpMethod.Post, new Uri(Api, "v1/payments/" + paymentId + "/refunds"));
        request.Headers.Add("X-Idempotency-Key", stableKey);
        request.Content = JsonContent.Create(new { });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await RequireSuccess(response, ct);
        var after = await ReadPayment(paymentId, ct);
        AssertMatches(after, expected);
        return after;
    }

    private async Task<VerifiedSandboxPayment> ReadPayment(string paymentId, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get, new Uri(Api, "v1/payments/" + paymentId));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await RequireSuccess(response, ct);
        using var document = await ReadBounded(response, ct);
        var json = document.RootElement;
        if (!json.TryGetProperty("live_mode", out var live) || live.ValueKind != JsonValueKind.False)
            throw new InvalidOperationException("Se rechazó un pago productivo o sin evidencia explícita de modo de pruebas.");
        if (!json.TryGetProperty("collector_id", out var seller) || !seller.TryGetInt64(out var sellerId) || sellerId != options.ExpectedTestSellerId)
            throw new InvalidOperationException("El pago pertenece a otro vendedor.");
        var returnedId = PaymentId(json);
        if (!json.TryGetProperty("transaction_amount", out var amountValue) || !amountValue.TryGetDecimal(out var amount) || amount <= 0 || decimal.Round(amount, 2) != amount)
            throw new InvalidOperationException("El pago tiene un importe inválido.");
        var currency = RequiredString(json, "currency_id");
        var reference = RequiredString(json, "external_reference");
        if (returnedId != paymentId || currency != "COP")
            throw new InvalidOperationException("El pago tiene un identificador o moneda no permitido.");
        var status = RequiredString(json, "status");
        if (status is not ("pending" or "approved" or "rejected" or "cancelled" or "in_process" or "refunded" or "charged_back" or "authorized"))
            throw new InvalidOperationException("Estado de pago desconocido; requiere conciliación.");
        return new VerifiedSandboxPayment(paymentId, status, amount, currency, reference);
    }

    private SandboxCheckout ReadCheckout(JsonElement json, SandboxCheckoutRequest expected, bool requireFullMatch)
    {
        if (!json.TryGetProperty("collector_id", out var seller) || !seller.TryGetInt64(out var sellerId) || sellerId != options.ExpectedTestSellerId)
            throw new InvalidOperationException("La preferencia no pertenece al vendedor de prueba configurado.");
        var id = RequiredString(json, "id");
        ValidatePreferenceId(id);
        if (!Uri.TryCreate(RequiredString(json, "init_point"), UriKind.Absolute, out var checkout) ||
            checkout.Scheme != "https" || checkout.UserInfo.Length != 0 || checkout.Port != 443 || checkout.Fragment.Length != 0 ||
            checkout.Host is not ("www.mercadopago.com.co" or "www.mercadopago.com" or "sandbox.mercadopago.com"))
            throw new InvalidOperationException("El proveedor devolvió un destino de checkout no permitido.");
        if (requireFullMatch)
        {
            if (RequiredString(json, "external_reference") != Reference(expected) ||
                !json.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0 || items.GetArrayLength() > 100)
                throw new InvalidOperationException("La preferencia no coincide con la orden esperada.");
            decimal total = 0;
            foreach (var item in items.EnumerateArray())
            {
                if (RequiredString(item, "currency_id") != expected.Currency ||
                    !item.TryGetProperty("quantity", out var quantity) || !quantity.TryGetInt32(out var units) || units <= 0 ||
                    !item.TryGetProperty("unit_price", out var price) || !price.TryGetDecimal(out var unitPrice) || unitPrice <= 0 || decimal.Round(unitPrice, 2) != unitPrice)
                    throw new InvalidOperationException("La preferencia contiene importes o moneda inesperados.");
                total = checked(total + units * unitPrice);
            }
            if (total != expected.Amount || !json.TryGetProperty("expires", out var expires) || expires.ValueKind != JsonValueKind.True ||
                !DateTimeOffset.TryParse(RequiredString(json, "expiration_date_from"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
                !DateTimeOffset.TryParse(RequiredString(json, "expiration_date_to"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) ||
                to <= from || to - from > TimeSpan.FromMinutes(15) ||
                expected.ExpiresAt.HasValue && Math.Abs((to - expected.ExpiresAt.Value).TotalMilliseconds) > 1)
                throw new InvalidOperationException("La preferencia tiene importe o vencimiento inesperado.");
        }
        return new SandboxCheckout(id, checkout, Reference(expected), true);
    }

    private async Task VerifyTestSeller(CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get, SellerIdentity);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await RequireSuccess(response, ct);
        using var document = await ReadBounded(response, ct);
        var json = document.RootElement;
        if (!json.TryGetProperty("id", out var id) || !id.TryGetInt64(out var sellerId) || sellerId != options.ExpectedTestSellerId ||
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
            if (url is null || !url.IsAbsoluteUri || url.Scheme != "https" || url.UserInfo.Length != 0 || url.Port != 443 || url.IsLoopback || url.Fragment.Length != 0 ||
                url.HostNameType != UriHostNameType.Dns || !url.Host.Contains('.') || url.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                url.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Las URLs de retorno y notificación requieren HTTPS público sin credenciales ni fragmento.");
    }
    private static void ValidateInput(SandboxCheckoutRequest input)
    {
        if (input.OrderId == Guid.Empty || input.PartnerId == Guid.Empty || input.Amount <= 0 || input.Amount > 10_000_000m || decimal.Round(input.Amount, 2) != input.Amount || input.Currency != "COP")
            throw new ArgumentException("Orden/partner, importe o moneda de prueba inválidos.", nameof(input));
    }
    public static string Reference(SandboxCheckoutRequest input) => $"{input.PartnerId:N}:{input.OrderId:N}";
    private static void ValidatePaymentId(string paymentId)
    {
        if (string.IsNullOrWhiteSpace(paymentId) || paymentId.Length > 24 || paymentId.Any(c => c is < '0' or > '9'))
            throw new ArgumentException("El identificador de pago debe ser numérico.", nameof(paymentId));
    }
    private static void ValidatePreferenceId(string id)
    {
        if (id.Length > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new InvalidOperationException("El identificador de preferencia es inválido.");
    }
    private static string PaymentId(JsonElement json) => json.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number
        ? id.ToString() : throw new InvalidOperationException("El proveedor omitió el identificador de pago.");
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
