using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

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
            builder.ConfigureTestServices(services => services.PostConfigure<JwtBearerOptions>("oidc", options =>
            {
                options.Configuration = discovery;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(discovery);
            }));
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
        await Check("Public auth config advertises demo disabled", async () =>
        {
            using var response = await client.GetAsync("/api/v1/auth/config");
            using var config = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return response.IsSuccessStatusCode && config.RootElement.GetProperty("demoEnabled").GetBoolean() == false;
        });
        var path = Environment.GetEnvironmentVariable("QA_API_RESULTS_PATH") ?? Path.Combine(root, "test-results/api-boundary-results.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { measuredAt = DateTimeOffset.UtcNow, transport = "ASP.NET TestServer (in memory)", oidcDiscovery = "static ephemeral RSA fixture; real JWT validation middleware", databaseRequired = false, passed = results.Count - failures, failed = failures, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"API boundary checks: {results.Count - failures}/{results.Count} passed.");
        return failures == 0 ? 0 : 1;
    }
}
