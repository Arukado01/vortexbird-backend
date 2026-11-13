using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using VortexBird.Infrastructure.Payments;
using VortexBird.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VortexBird.Application.Abstractions;

var checks = new List<object>();
var order = new SandboxCheckoutRequest(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), 69800m);
var options = new MercadoPagoSandboxOptions(true, "APP_USR-fixture-not-a-real-secret", 123, new Uri("https://test.example.com/webhooks/mp"), new Uri("https://test.example.com/checkout/result"));
var reference = $"{order.PartnerId:N}:{order.OrderId:N}";
const string seller = "{\"id\":123,\"tags\":[\"test_user\"]}";
const string preference = "{\"id\":\"pref-test\",\"collector_id\":123,\"init_point\":\"https://www.mercadopago.com.co/checkout/v1/redirect?pref_id=pref-test\"}";
string Payment(bool live = false, decimal amount = 69800, string? external = null, long collector = 123, string status = "approved") => JsonSerializer.Serialize(new { id = 456, collector_id = collector, live_mode = live, transaction_amount = amount, currency_id = "COP", external_reference = external ?? reference, status });

async Task Check(string name, Func<Task> test)
{
    try { await test(); checks.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { checks.Add(new { name, passed = false }); Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message); }
}
void True(bool result) { if (!result) throw new Exception("Assertion failed"); }
async Task Reject(Func<Task> action) { try { await action(); } catch (InvalidOperationException) { return; } catch (ArgumentException) { return; } throw new Exception("Expected fail-closed rejection"); }
MercadoPagoSandboxClient Client(SequenceHandler handler, MercadoPagoSandboxOptions? config = null) => new(new HttpClient(handler), config ?? options);

await Check("disabled sandbox makes no HTTP request", async () => { var h = new SequenceHandler(); await Reject(() => Client(h, options with { Enabled = false }).CreateCheckout(order)); True(h.Calls.Count == 0); });
await Check("missing token makes no HTTP request", async () => { var h = new SequenceHandler(); await Reject(() => Client(h, options with { AccessToken = "" }).CreateCheckout(order)); True(h.Calls.Count == 0); });
await Check("insecure callback rejected before request", async () => { var h = new SequenceHandler(); await Reject(() => Client(h, options with { NotificationUrl = new Uri("http://example.com/hook") }).CreateCheckout(order)); True(h.Calls.Count == 0); });
await Check("invalid amount rejected before request", async () => { var h = new SequenceHandler(); await Reject(() => Client(h).CreateCheckout(order with { Amount = -1 })); True(h.Calls.Count == 0); });
await Check("real seller cannot create preference", async () => { var h = new SequenceHandler("{\"id\":123,\"tags\":[\"user_info_verified\"]}"); await Reject(() => Client(h).CreateCheckout(order)); True(h.Calls.Count == 1 && h.Calls[0].Method == "GET"); });
await Check("wrong test seller cannot create preference", async () => { var h = new SequenceHandler("{\"id\":321,\"tags\":[\"test_user\"]}"); await Reject(() => Client(h).CreateCheckout(order)); True(h.Calls.Count == 1); });
await Check("verified test seller APP_USR can create hosted checkout", async () => { var h = new SequenceHandler(seller, preference); var result = await Client(h).CreateCheckout(order); True(result.PreferenceId == "pref-test" && result.TestSellerVerified && result.ExternalReference == reference); True(h.Calls[0].Url == "https://api.mercadolibre.com/users/me"); True(h.Calls[1].Url == "https://api.mercadopago.com/checkout/preferences"); using var body = JsonDocument.Parse(h.Calls[1].Body!); True(body.RootElement.GetProperty("external_reference").GetString() == reference); True(!h.Calls[1].Body!.Contains("card_number", StringComparison.Ordinal)); });
await Check("checkout redirect outside provider rejected", async () => { var h = new SequenceHandler(seller, preference.Replace("www.mercadopago.com.co", "evil.example.com")); await Reject(() => Client(h).CreateCheckout(order)); });
await Check("preference seller mismatch rejected", async () => { var h = new SequenceHandler(seller, preference.Replace("123", "321")); await Reject(() => Client(h).CreateCheckout(order)); });
await Check("read matching sandbox payment from authoritative API", async () => { var h = new SequenceHandler(seller, Payment()); var result = await Client(h).ReadVerifiedPayment("456", order); True(result.Status == "approved" && result.Amount == order.Amount); });
await Check("live_mode payment rejected", async () => { var h = new SequenceHandler(seller, Payment(live: true)); await Reject(() => Client(h).ReadVerifiedPayment("456", order)); });
await Check("payment amount mismatch rejected", async () => { var h = new SequenceHandler(seller, Payment(amount: 1)); await Reject(() => Client(h).ReadVerifiedPayment("456", order)); });
await Check("cross-partner order reference rejected", async () => { var h = new SequenceHandler(seller, Payment(external: "another-partner:another-order")); await Reject(() => Client(h).ReadVerifiedPayment("456", order)); });
await Check("payment seller mismatch rejected", async () => { var h = new SequenceHandler(seller, Payment(collector: 321)); await Reject(() => Client(h).ReadVerifiedPayment("456", order)); });
await Check("unknown provider status rejected", async () => { var h = new SequenceHandler(seller, Payment(status: "new-unmapped-state")); await Reject(() => Client(h).ReadVerifiedPayment("456", order)); });
await Check("payment path injection rejected before HTTP", async () => { var h = new SequenceHandler(); await Reject(() => Client(h).ReadVerifiedPayment("../users", order)); True(h.Calls.Count == 0); });

const string secret = "unit-test-webhook-secret-not-a-credential";
var now = DateTimeOffset.FromUnixTimeSeconds(1794585600);
string Sign(string timestamp, string request = "request-1", string id = "456") => "ts=" + timestamp + ",v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"id:{id};request-id:{request};ts:{timestamp};"))).ToLowerInvariant();
await Check("signature accepts valid second timestamp", () => { True(MercadoPagoWebhookSignature.Validate(Sign(now.ToUnixTimeSeconds().ToString()), "request-1", "456", secret, now)); return Task.CompletedTask; });
await Check("signature accepts valid millisecond timestamp", () => { True(MercadoPagoWebhookSignature.Validate(Sign(now.ToUnixTimeMilliseconds().ToString()), "request-1", "456", secret, now)); return Task.CompletedTask; });
await Check("tampered signed payment ID rejected", () => { True(!MercadoPagoWebhookSignature.Validate(Sign(now.ToUnixTimeSeconds().ToString()), "request-1", "457", secret, now)); return Task.CompletedTask; });
await Check("expired signed webhook rejected", () => { True(!MercadoPagoWebhookSignature.Validate(Sign(now.AddMinutes(-6).ToUnixTimeSeconds().ToString()), "request-1", "456", secret, now)); return Task.CompletedTask; });
await Check("future signed webhook rejected", () => { True(!MercadoPagoWebhookSignature.Validate(Sign(now.AddMinutes(6).ToUnixTimeSeconds().ToString()), "request-1", "456", secret, now)); return Task.CompletedTask; });
await Check("duplicate signature timestamp rejected", () => { var stamp=now.ToUnixTimeSeconds().ToString(); True(!MercadoPagoWebhookSignature.Validate(Sign(stamp)+",ts="+stamp, "request-1", "456", secret, now)); return Task.CompletedTask; });
await Check("missing request ID rejected", () => { True(!MercadoPagoWebhookSignature.Validate(Sign(now.ToUnixTimeSeconds().ToString()), null, "456", secret, now)); return Task.CompletedTask; });
await Check("malformed hexadecimal signature rejected", () => { True(!MercadoPagoWebhookSignature.Validate("ts="+now.ToUnixTimeSeconds()+",v1="+new string('z',64), "request-1", "456", secret, now)); return Task.CompletedTask; });
await Check("private callback IP rejected before HTTP", async () => { var h = new SequenceHandler(); await Reject(() => Client(h, options with { NotificationUrl = new Uri("https://169.254.169.254/hook") }).CreateCheckout(order)); True(h.Calls.Count == 0); });
await Check("preference expires with reservation deadline", async () =>
{
    var expiration = DateTimeOffset.UtcNow.AddMinutes(10);
    var h = new SequenceHandler(seller, preference); await Client(h).CreateCheckout(order with { ExpiresAt = expiration });
    using var body = JsonDocument.Parse(h.Calls[1].Body!);
    True(body.RootElement.GetProperty("expires").GetBoolean());
    True(Math.Abs((DateTimeOffset.Parse(body.RootElement.GetProperty("expiration_date_to").GetString()!) - expiration).TotalMilliseconds) < 1);
});
await Check("expired reservation cannot create checkout", async () => { var h = new SequenceHandler(); await Reject(() => Client(h).CreateCheckout(order with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) })); True(h.Calls.Count == 0); });
await Check("unknown creation search absent never repeats POST", async () =>
{
    var h = new SequenceHandler(seller, "{\"total\":0,\"elements\":[]}");
    True(await Client(h).SearchCheckout(order) is null); True(h.Calls.All(x => x.Method == "GET"));
});
await Check("ambiguous existing preferences fail closed", async () =>
{
    var h = new SequenceHandler(seller, "{\"total\":2,\"elements\":[{\"id\":\"pref-a\"},{\"id\":\"pref-b\"}]}");
    await Reject(() => Client(h).SearchCheckout(order)); True(h.Calls.All(x => x.Method == "GET"));
});
await Check("lost checkout response recovered with correlated GET", async () =>
{
    var expiration = DateTimeOffset.UtcNow.AddMinutes(10);
    var detail = JsonSerializer.Serialize(new { id = "pref-test", collector_id = 123, init_point = "https://www.mercadopago.com.co/checkout/v1/redirect?pref_id=pref-test", external_reference = reference,
        items = new[] { new { quantity = 1, unit_price = order.Amount, currency_id = "COP" } }, expires = true, expiration_date_from = expiration.AddMinutes(-15), expiration_date_to = expiration });
    var h = new SequenceHandler(seller, "{\"total\":1,\"elements\":[{\"id\":\"pref-test\"}]}", detail);
    var recovered = await Client(h).SearchCheckout(order with { ExpiresAt = expiration });
    True(recovered?.PreferenceId == "pref-test" && h.Calls.Count == 3 && h.Calls.All(x => x.Method == "GET"));
});
await Check("search individually verifies every payment and retains duplicate approvals", async () =>
{
    var h = new SequenceHandler(seller, "{\"paging\":{\"total\":2,\"offset\":0},\"results\":[{\"id\":457},{\"id\":456}]}", Payment().Replace("\"id\":456", "\"id\":457"), Payment());
    var found = await Client(h).SearchPayments(order);
    True(found.Count == 2 && found[0].PaymentId == "456" && h.Calls.Count == 4 && h.Calls.All(x => x.Method == "GET"));
});
await Check("payment search truncation fails closed", async () => { var h = new SequenceHandler(seller, "{\"paging\":{\"total\":51,\"offset\":0},\"results\":[]}"); await Reject(() => Client(h).SearchPayments(order)); });
await Check("payment search cannot cross tenant via returned ID", async () => { var h = new SequenceHandler(seller, "{\"paging\":{\"total\":1,\"offset\":0},\"results\":[{\"id\":456}]}", Payment(external: "other")); await Reject(() => Client(h).SearchPayments(order)); });
await Check("refund sends stable key only after verification and rereads outcome", async () =>
{
    var h = new SequenceHandler(seller, Payment(), "{\"status\":\"approved\"}", Payment(status: "refunded"));
    var result = await Client(h).RefundVerifiedPayment("456", order, "stable-fixture-key");
    True(result.Status == "refunded" && h.Calls.Count == 4 && h.Calls[2].Method == "POST" && h.Calls[2].Body == "{}");
    True(h.IdempotencyKeys[2] == "stable-fixture-key" && h.Calls[2].Url.EndsWith("/v1/payments/456/refunds"));
});
await Check("refund accepted response alone is not confirmation", async () => { var h = new SequenceHandler(seller, Payment(), "{\"status\":\"approved\"}", Payment()); var result = await Client(h).RefundVerifiedPayment("456", order, "stable-fixture-key"); True(result.Status == "approved"); });
await Check("already refunded payment replay makes no mutation", async () => { var h = new SequenceHandler(seller, Payment(status: "refunded")); var result = await Client(h).RefundVerifiedPayment("456", order, "stable-fixture-key"); True(result.Status == "refunded" && h.Calls.All(x => x.Method == "GET")); });
await Check("refund refuses unapproved payment", async () => { var h = new SequenceHandler(seller, Payment(status: "pending")); await Reject(() => Client(h).RefundVerifiedPayment("456", order, "stable-fixture-key")); True(h.Calls.All(x => x.Method == "GET")); });
await Check("refund refuses amount mismatch before mutation", async () => { var h = new SequenceHandler(seller, Payment(amount: 10)); await Reject(() => Client(h).RefundVerifiedPayment("456", order, "stable-fixture-key")); True(h.Calls.All(x => x.Method == "GET")); });

var policyCases = new (string Name, string Evidence, string Order, string? Accepted, string? Previous, bool Reserved, PaymentAction Expected)[]
{
    ("valid approval confirms once", "approved", OrderStates.AwaitingPayment, null, "pending", true, PaymentAction.Confirm),
    ("callback may beat order-created consumer", "approved", OrderStates.Reserved, null, null, true, PaymentAction.Confirm),
    ("late approval after cancellation refunds", "approved", OrderStates.Cancelled, null, "pending", false, PaymentAction.Refund),
    ("approval with expired reservation refunds", "approved", OrderStates.AwaitingPayment, null, "pending", false, PaymentAction.Refund),
    ("duplicate approved provider payment refunds", "approved", OrderStates.Confirmed, "999", null, false, PaymentAction.Refund),
    ("accepted payment replay does not consume again", "approved", OrderStates.Confirmed, "456", "approved", false, PaymentAction.Ignore),
    ("late pending cannot downgrade approved", "pending", OrderStates.Confirmed, "456", "approved", false, PaymentAction.Ignore),
    ("late rejection cannot downgrade approved", "rejected", OrderStates.Confirmed, "456", "approved", false, PaymentAction.Ignore),
    ("old approval cannot undo refund", "approved", OrderStates.Cancelled, "456", "refunded", false, PaymentAction.Ignore),
    ("old approval cannot undo chargeback", "approved", OrderStates.Confirmed, "456", "charged_back", false, PaymentAction.Ignore),
    ("pending rejection cancels and releases", "rejected", OrderStates.AwaitingPayment, null, "pending", true, PaymentAction.Cancel),
    ("other failed payment cannot cancel accepted order", "rejected", OrderStates.Confirmed, "999", null, false, PaymentAction.Ignore),
    ("authoritative refund triggers compensation", "refunded", OrderStates.Confirmed, "456", "approved", false, PaymentAction.RecordRefund),
    ("duplicate refund cannot restock twice", "refunded", OrderStates.Cancelled, "456", "refunded", false, PaymentAction.Ignore),
    ("chargeback requires review", "charged_back", OrderStates.Shipped, "456", "approved", false, PaymentAction.ReviewChargeback)
};
foreach (var test in policyCases)
    await Check(test.Name, () => { True(PaymentOutcomePolicy.Decide(test.Evidence, test.Order, test.Accepted, "456", test.Previous, test.Reserved) == test.Expected); return Task.CompletedTask; });

var callbackBody = "{\"type\":\"payment\",\"live_mode\":false,\"user_id\":123,\"data\":{\"id\":\"456\"}}";
var signed = Sign(now.ToUnixTimeSeconds().ToString());
AuthenticatedPaymentNotification? Parse(string body, string? signature = null) => PaymentWebhookReceiptParser.Parse(body, signature ?? signed, "request-1", "456", secret, 123, now);
await Check("valid signed notification produces minimal durable receipt", () => { var receipt = Parse(callbackBody); True(receipt?.PaymentId == "456" && receipt.NotificationKey.Length == 64); return Task.CompletedTask; });
await Check("equivalent signature encodings dedupe to same manifest", () => { var parts = signed.Split(','); var reordered = " " + parts[1].ToUpperInvariant().Replace("V1=", "v1=") + " , " + parts[0] + " "; True(Parse(callbackBody)?.NotificationKey == Parse(callbackBody, reordered)?.NotificationKey); return Task.CompletedTask; });
await Check("callback rejects unsigned body identity mismatch", () => { True(Parse(callbackBody.Replace("\"456\"", "\"999\"")) is null); return Task.CompletedTask; });
await Check("callback rejects production and foreign seller envelopes", () => { True(Parse(callbackBody.Replace("false", "true")) is null && Parse(callbackBody.Replace("123", "321")) is null); return Task.CompletedTask; });
await Check("callback rejects malformed oversized and unrelated topics", () => { True(Parse("{") is null && Parse(new string('x', 32769)) is null && Parse(callbackBody.Replace("\"payment\"", "\"merchant_order\"")) is null); return Task.CompletedTask; });
await Check("browser or body approval is not accepted as payment evidence", () => { var receipt = Parse(callbackBody[..^1] + ",\"status\":\"approved\"}"); True(receipt?.PaymentId == "456" && receipt.GetType().GetProperties().All(x => x.Name != "Status")); return Task.CompletedTask; });
await Check("default registration keeps external adapter disabled", () => { var services = new ServiceCollection(); services.AddPaymentProcessing(new ConfigurationBuilder().Build()); True(!((PaymentRuntimeOptions)services.Single(x => x.ServiceType == typeof(PaymentRuntimeOptions)).ImplementationInstance!).Enabled); True(services.All(x => x.ServiceType != typeof(IPaymentGateway))); return Task.CompletedTask; });
await Check("sandbox registration requires explicit enable and credentials", async () => { var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Payments:Provider"] = "MercadoPagoSandbox" }).Build(); await Reject(() => { new ServiceCollection().AddPaymentProcessing(config); return Task.CompletedTask; }); });
var json = JsonSerializer.Serialize(new { transport = "fake HttpMessageHandler, no network", liveSandboxVerified = false, checks }, new JsonSerializerOptions { WriteIndented = true });
var output = args.FirstOrDefault(x => x.StartsWith("--report="))?[9..];
if (output is not null) File.WriteAllText(output, json);
Console.WriteLine($"{checks.Count} checks completed; external sandbox not contacted.");
return json.Contains("\"passed\": false", StringComparison.Ordinal) ? 1 : 0;

sealed class SequenceHandler(params string[] responses) : HttpMessageHandler
{
    public List<(string Method, string Url, string? Body)> Calls { get; } = [];
    public List<string?> IdempotencyKeys { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
        IdempotencyKeys.Add(request.Headers.TryGetValues("X-Idempotency-Key", out var values) ? values.Single() : null);
        if (request.Headers.Authorization?.Scheme != "Bearer") throw new Exception("Missing bearer authentication");
        var index = Calls.Count - 1;
        if (index >= responses.Length) throw new Exception("Unexpected HTTP call");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[index], Encoding.UTF8, "application/json") };
    }
}
