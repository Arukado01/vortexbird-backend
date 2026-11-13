using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using VortexBird.Infrastructure.Payments;

namespace VortexBird.QA.ApiBoundary;

public static class EntryPoint
{
    public static async Task<int> Main()
    {
        const string issuer = "https://oidc.qa.invalid/realms/vortexbird";
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "qa-ephemeral" };
        var discovery = new OpenIdConnectConfiguration { Issuer = issuer };
        discovery.SigningKeys.Add(key);
        Environment.SetEnvironmentVariable("Workers__Enabled", "false");
        Environment.SetEnvironmentVariable("Database__Initialize", "false");
        Environment.SetEnvironmentVariable("Authentication__DemoEnabled", "false");
        Environment.SetEnvironmentVariable("Authentication__Authority", issuer);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");

        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseContentRoot(Path.Combine(root, "src/VortexBird.API"));
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Workers:Enabled"] = "false", ["Database:Initialize"] = "false",
                ["Authentication:DemoEnabled"] = "false", ["Authentication:Authority"] = issuer,
                ["Authentication:Audience"] = "vortexbird-api", ["Authentication:ValidIssuer"] = issuer,
                ["ConnectionStrings:Database"] = "Host=127.0.0.1;Port=59999;Database=vortexbird;Username=vortexbird;Timeout=1;Command Timeout=1"
            }));
            // Only discovery transport is replaced: the API's JWT validation flags,
            // audience, issuer, middleware and routes remain the production code.
            builder.ConfigureTestServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.Configure<KeyManagementOptions>(options => options.XmlRepository = new InMemoryKeyRepository());
                services.PostConfigure<JwtBearerOptions>("oidc", options =>
                {
                    options.Configuration = discovery;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(discovery);
                });
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var results = new List<object>();
        var failures = 0;
        async Task Check(string name, Func<Task<bool>> check)
        {
            try
            {
                var passed = await check();
                results.Add(new { name, status = passed ? "PASSED" : "FAILED" });
                if (!passed) failures++;
                Console.WriteLine($"{(passed ? "PASSED" : "FAILED")} {name}");
            }
            catch (Exception error)
            {
                failures++;
                results.Add(new { name, status = "FAILED", error = error.Message });
                Console.Error.WriteLine($"FAILED {name}: {error.Message}");
            }
        }
        async Task<bool> Denied(string? token = null, bool spoof = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/orders");
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (spoof) { request.Headers.Add("X-User-ID", "demo-admin"); request.Headers.Add("X-Permissions", "*"); request.Headers.Add("X-Partner-ID", Guid.NewGuid().ToString()); }
            using var response = await client.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.Unauthorized) return false;
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return response.Content.Headers.ContentType?.MediaType == "application/problem+json"
                && error.RootElement.GetProperty("status").GetInt32() == 401
                && error.RootElement.TryGetProperty("correlationId", out var correlation)
                && !string.IsNullOrWhiteSpace(correlation.GetString());
        }
        string Token(string tokenIssuer, string audience, DateTime expires, SigningCredentials? signing = null)
        {
            var jwt = new JwtSecurityToken(tokenIssuer, audience,
                [new Claim("sub", "qa-unprovisioned"), new Claim("identity_type", "human")],
                notBefore: DateTime.UtcNow.AddHours(-2), expires: expires,
                signingCredentials: signing ?? new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
            return new JwtSecurityTokenHandler().WriteToken(jwt);
        }
        await Check("Anonymous API request returns RFC9457 401", () => Denied());
        await Check("Forged identity/permission headers cannot authenticate", () => Denied(spoof: true));
        await Check("Malformed token rejected", () => Denied("not-a-valid-jwt"));
        await Check("Expired correctly signed token rejected", () => Denied(Token(issuer, "vortexbird-api", DateTime.UtcNow.AddMinutes(-5))));
        await Check("Wrong issuer rejected despite valid signature", () => Denied(Token("https://wrong.qa.invalid", "vortexbird-api", DateTime.UtcNow.AddMinutes(5))));
        await Check("Wrong audience rejected despite valid signature", () => Denied(Token(issuer, "wrong-api", DateTime.UtcNow.AddMinutes(5))));
        using var otherRsa = RSA.Create(2048);
        await Check("Unknown signing key rejected", () => Denied(Token(issuer, "vortexbird-api", DateTime.UtcNow.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(otherRsa) { KeyId = "untrusted" }, SecurityAlgorithms.RsaSha256))));
        await Check("Production liveness is public", async () => (await client.GetAsync("/health/live")).StatusCode == HttpStatusCode.OK);
        await Check("Production has no demo-login endpoint", async () => (await client.PostAsync("/api/v1/auth/demo-login", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode == HttpStatusCode.NotFound);
        await Check("Production OpenAPI endpoint is not exposed", async () => (await client.GetAsync("/openapi/v1.json")).StatusCode == HttpStatusCode.NotFound);
        await Check("Development serves its generated OpenAPI document", async () =>
        {
            await using var developmentFactory = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
            using var developmentClient = developmentFactory.CreateClient();
            using var response = await developmentClient.GetAsync("/openapi/v1.json");
            if (response.StatusCode != HttpStatusCode.OK) return false;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.GetProperty("paths").TryGetProperty("/api/v1/orders", out _);
        });
        await Check("Public auth config advertises demo disabled", async () =>
        {
            using var response = await client.GetAsync("/api/v1/auth/config");
            using var config = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return response.IsSuccessStatusCode && config.RootElement.GetProperty("demoEnabled").GetBoolean() == false;
        });
        const string callbackPath = "/api/v1/payments/mercadopago/webhook";
        await Check("Disabled payment callback has no public acceptance path", async () =>
            (await client.PostAsync(callbackPath, new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode == HttpStatusCode.NotFound);

        const string callbackSecret = "qa-webhook-secret-at-least-32-characters-not-a-credential";
        await using var callbackFactory = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // Only test-account configuration changes. Execute the actual mapped callback,
            // signature verifier/body limiter; every case must reject before persistence.
            services.RemoveAll<PaymentRuntimeOptions>();
            services.RemoveAll<MercadoPagoSandboxOptions>();
            services.AddSingleton(new PaymentRuntimeOptions(true, callbackSecret));
            services.AddSingleton(new MercadoPagoSandboxOptions(true, "qa-not-a-real-token", 123,
                new Uri("https://callback.qa.invalid/notification"), new Uri("https://callback.qa.invalid/return")));
        }));
        using var callbackClient = callbackFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        string SignCallback()
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(callbackSecret), Encoding.UTF8.GetBytes($"id:456;request-id:qa-callback-1;ts:{timestamp};"));
            return $"ts={timestamp},v1={Convert.ToHexString(digest).ToLowerInvariant()}";
        }
        async Task<bool> CallbackReject(string body, HttpStatusCode expected, string? signature, string query = "?data.id=456", bool streamed = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, callbackPath + query);
            request.Headers.Add("x-request-id", "qa-callback-1");
            if (signature is not null) request.Headers.Add("x-signature", signature);
            request.Content = streamed ? new UnknownLengthContent(body) : new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await callbackClient.SendAsync(request);
            return response.StatusCode == expected;
        }
        const string notification = "{\"type\":\"payment\",\"live_mode\":false,\"user_id\":123,\"data\":{\"id\":\"456\"}}";
        await Check("Enabled callback rejects missing signature before database access", () => CallbackReject(notification, HttpStatusCode.Unauthorized, null));
        await Check("Enabled callback rejects forged signature before database access", () => CallbackReject(notification, HttpStatusCode.Unauthorized, "ts=123,v1=forged"));
        await Check("Signed callback cannot use ambiguous duplicate payment query IDs", () => CallbackReject(notification, HttpStatusCode.Unauthorized, SignCallback(), "?data.id=456&data.id=456"));
        await Check("Signed callback rejects malformed JSON", () => CallbackReject("{", HttpStatusCode.BadRequest, SignCallback()));
        await Check("Signed callback rejects body/query payment mismatch", () => CallbackReject(notification.Replace("\"456\"", "\"457\""), HttpStatusCode.BadRequest, SignCallback()));
        await Check("Signed callback rejects live payment notifications", () => CallbackReject(notification.Replace("false", "true"), HttpStatusCode.BadRequest, SignCallback()));
        await Check("Signed callback rejects a different seller", () => CallbackReject(notification.Replace("123", "321"), HttpStatusCode.BadRequest, SignCallback()));
        await Check("Signed callback bounds declared request size", () => CallbackReject(new string(' ', 32769), HttpStatusCode.RequestEntityTooLarge, SignCallback()));
        await Check("Signed callback bounds unknown-length streamed body", () => CallbackReject(new string(' ', 40000), HttpStatusCode.RequestEntityTooLarge, SignCallback(), streamed: true));
        var path = Environment.GetEnvironmentVariable("QA_API_RESULTS_PATH") ?? Path.Combine(root, "test-results/api-boundary-results.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { measuredAt = DateTimeOffset.UtcNow, transport = "ASP.NET TestServer (in memory)", oidcDiscovery = "static ephemeral RSA fixture; real JWT validation middleware", paymentCallback = "real callback rejection paths; no accepted/durable receipt or provider call", databaseRequired = false, passed = results.Count - failures, failed = failures, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"API boundary checks: {results.Count - failures}/{results.Count} passed.");
        return failures == 0 ? 0 : 1;
    }
}

internal sealed class UnknownLengthContent(string body) : HttpContent
{
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(Encoding.UTF8.GetBytes(body)).AsTask();
}

// Test-host startup also initializes its key-ring hosted service. Keep that fixture
// entirely in memory rather than writing unrelated host-machine profile keys.
internal sealed class InMemoryKeyRepository : IXmlRepository
{
    private readonly List<XElement> entries = [];
    public IReadOnlyCollection<XElement> GetAllElements() { lock (entries) return entries.Select(x => new XElement(x)).ToArray(); }
    public void StoreElement(XElement element, string friendlyName) { lock (entries) entries.Add(new XElement(element)); }
}
