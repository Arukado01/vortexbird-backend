using System.Net;
using System.Text.Json;
using VortexBird.Infrastructure;
using VortexBird.Application.Abstractions;

var failed = new List<string>();
var results = new List<object>();
var checks = 0;
void Check(bool condition, string name)
{
    checks++;
    results.Add(new { name, passed = condition });
    if (!condition) { failed.Add(name); Console.Error.WriteLine($"FAILED {name}"); }
}

// Address policy tests exercise real webhook egress protections without network.
foreach (var ip in new[] { "127.0.0.1", "10.0.0.1", "172.16.0.1", "172.31.255.255", "192.168.1.1", "169.254.169.254", "100.64.0.1", "198.18.0.1", "0.0.0.0", "224.0.0.1", "255.255.255.255", "::1", "::", "fc00::1", "fe80::1", "ff02::1", "::ffff:127.0.0.1", "::ffff:10.0.0.1" })
    Check(!WebhookSafety.IsPublic(IPAddress.Parse(ip)), $"Deny private/reserved address {ip}");
foreach (var ip in new[] { "8.8.8.8", "1.1.1.1", "172.15.1.1", "172.32.1.1", "2606:4700:4700::1111" })
    Check(WebhookSafety.IsPublic(IPAddress.Parse(ip)), $"Permit public address {ip}");
foreach (var url in new[] { "http://example.com", "file:///etc/passwd", "https://127.0.0.1", "https://[::1]", "https://user:password@example.com", "https://example.com:8443", "//example.com/path", "not a URL" })
{
    var rejected = false;
    try { WebhookSafety.Validate(url); } catch (ArgumentException) { rejected = true; }
    Check(rejected, $"Reject unsafe webhook URL {url}");
}
Check(WebhookSafety.Validate("https://example.com/events").Scheme == "https", "Accept HTTPS hostname; actual DNS is checked at connection time");

// Real production scope resolver: signed claims may restrict, never broaden.
var partnerA = Guid.Parse("11111111-1111-1111-1111-111111111111");
var partnerB = Guid.Parse("22222222-2222-2222-2222-222222222222");
var channelA = Guid.Parse("33333333-3333-3333-3333-333333333333");
var channelB = Guid.Parse("44444444-4444-4444-4444-444444444444");
Check(AuthorizationScope.Resolve(null, null, []).IsGlobal, "Unrestricted global account remains global");
Check(AuthorizationScope.Resolve(partnerA, null, []).PartnerId == partnerA, "Absent token tenant never clears database tenant");
Check(AuthorizationScope.Resolve(null, partnerA.ToString(), []).PartnerId == partnerA, "Token partner narrows a global database account");
Check(!AuthorizationScope.Resolve(null, partnerA.ToString(), []).IsGlobal, "Token-scoped account cannot use global administration");
Check(AuthorizationScope.Resolve(partnerA, partnerA.ToString(), []).PartnerId == partnerA, "Matching database and token tenant is permitted");
var scoped = AuthorizationScope.Resolve(partnerA, null, [channelA.ToString(), channelB.ToString(), channelA.ToString()]);
Check(scoped.ChannelIds.Count == 2 && scoped.ChannelIds.Contains(channelA) && scoped.ChannelIds.Contains(channelB), "Channel restrictions remain intact and deduplicate");
Check(scoped.PartnerId == partnerA, "Channel restrictions cannot erase tenant scope");
void DenyScope(Guid? databasePartner, string? claimedPartner, IEnumerable<string> channels, string name)
{
    var rejected = false;
    try { AuthorizationScope.Resolve(databasePartner, claimedPartner, channels); }
    catch (AuthorizationScopeException) { rejected = true; }
    Check(rejected, name);
}
DenyScope(partnerA, partnerB.ToString(), [], "Different signed/database tenants are denied");
foreach (var invalid in new[] { "", " ", "garbage", Guid.Empty.ToString() })
{
    DenyScope(null, invalid, [], "Malformed partner claim cannot become global: " + invalid);
    DenyScope(partnerA, null, [invalid], "Malformed channel claim cannot become unrestricted: " + invalid);
}
DenyScope(null, null, [channelA.ToString()], "Channel-only global claim is denied");
DenyScope(Guid.Empty, null, [], "Empty database tenant is denied");

Console.WriteLine($"Security checks: {checks - failed.Count}/{checks} passed; no external network requests performed.");
var backendRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var output = Environment.GetEnvironmentVariable("QA_SECURITY_RESULTS_PATH") ?? Path.Combine(backendRoot, "test-results", "security-results.json");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, JsonSerializer.Serialize(new { measuredAt = DateTimeOffset.UtcNow, passed = checks - failed.Count, failed = failed.Count, networkCalls = 0, results }, new JsonSerializerOptions { WriteIndented = true }));
return failed.Count == 0 ? 0 : 1;
