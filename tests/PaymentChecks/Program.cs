using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using VortexBird.Infrastructure.Payments;

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
var json = JsonSerializer.Serialize(new { transport = "fake HttpMessageHandler, no network", liveSandboxVerified = false, checks }, new JsonSerializerOptions { WriteIndented = true });
var output = args.FirstOrDefault(x => x.StartsWith("--report="))?[9..];
if (output is not null) File.WriteAllText(output, json);
Console.WriteLine($"{checks.Count} checks completed; external sandbox not contacted.");
return json.Contains("\"passed\": false", StringComparison.Ordinal) ? 1 : 0;

sealed class SequenceHandler(params string[] responses) : HttpMessageHandler
{
    public List<(string Method, string Url, string? Body)> Calls { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
        if (request.Headers.Authorization?.Scheme != "Bearer") throw new Exception("Missing bearer authentication");
        var index = Calls.Count - 1;
        if (index >= responses.Length) throw new Exception("Unexpected HTTP call");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[index], Encoding.UTF8, "application/json") };
    }
}
