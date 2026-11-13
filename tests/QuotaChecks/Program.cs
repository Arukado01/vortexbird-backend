using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using VortexBird.API;
using VortexBird.Application.Abstractions;

var results = new List<object>();
var failures = 0;
void Check(string name, Action test)
{
    try { test(); results.Add(new { name, status = "passed" }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; results.Add(new { name, status = "failed", error = error.Message }); Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
}
void True(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
var partner = Guid.NewGuid();
var one = Guid.NewGuid();
var two = Guid.NewGuid();
ChannelQuota[] visible = [new(one, 10), new(two, 20)];
Check("Explicit channel spends its budget and the shared partner budget only", () => {
    var plan = QuotaPlanner.Plan(partner, 100, visible, one);
    True(plan.Count == 2 && plan[0] == new QuotaBudget("partner", 100) && plan[1] == new QuotaBudget($"channel:{one:N}", 10));
});
Check("Aggregate spends every visible channel once", () => {
    var plan = QuotaPlanner.Plan(partner, 100, visible, null);
    True(plan.Count == 3 && plan.Count(x => x.Key == "partner") == 1 && plan.Any(x => x.Key == $"channel:{two:N}" && x.Limit == 20));
});
Check("Restricted aggregate cannot consume an unlisted channel", () => {
    var plan = QuotaPlanner.Plan(partner, 100, [visible[1]], null);
    True(plan.Count == 2 && plan.All(x => x.Key != $"channel:{one:N}"));
});
Check("Foreign target is rejected instead of silently reducing budgets", () => Reject<ArgumentException>(() => QuotaPlanner.Plan(partner, 100, [visible[0]], two)));
Check("Empty channel scope still enforces partner limit", () => True(QuotaPlanner.Plan(partner, 100, [], null).SequenceEqual([new QuotaBudget("partner", 100)])));
Check("Duplicate channels are rejected", () => Reject<ArgumentException>(() => QuotaPlanner.Plan(partner, 100, [visible[0], visible[0]], null)));
Check("Empty partner rejected", () => Reject<ArgumentException>(() => QuotaPlanner.Plan(Guid.Empty, 100, visible, null)));
Check("Invalid configured partner/channel quotas fail closed", () => {
    foreach (var limit in new[] { -1, 0, 9, 100001 }) {
        Reject<ArgumentException>(() => QuotaPlanner.Plan(partner, limit, visible, null));
        Reject<ArgumentException>(() => QuotaPlanner.Plan(partner, 100, [new ChannelQuota(one, limit)], null));
    }
});
Check("Query GUID accepts canonical variations but normalizes identity", () => True(PartnerQuota.OptionalGuid(new StringValues(one.ToString("D").ToUpperInvariant()), "channelId") == one));
Check("Ambiguous duplicate channel query is rejected", () => Reject<RequestInputException>(() => PartnerQuota.OptionalGuid(new StringValues([one.ToString(), two.ToString()]), "channelId")));
Check("Empty/malformed explicit channel cannot become an aggregate", () => {
    foreach (var text in new[] { "", "not-a-guid", Guid.Empty.ToString() }) Reject<RequestInputException>(() => PartnerQuota.OptionalGuid(text, "channelId"));
});

DefaultHttpContext Context(string body, bool declareLength = true) {
    var context = new DefaultHttpContext();
    context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
    context.Request.ContentType = "application/json";
    if (declareLength) context.Request.ContentLength = context.Request.Body.Length;
    return context;
}
var valid = JsonSerializer.Serialize(new { partnerId = partner, channelId = one, items = new[] { new { sku = "ONE", quantity = 1 } } });
Check("Order body scope extraction preserves payload for model binding", () => {
    var context = Context(valid);
    var target = PartnerQuota.ReadOrderTarget(context.Request, CancellationToken.None).GetAwaiter().GetResult();
    True(target == new QuotaRequestTarget(partner, one) && context.Request.Body.Position == 0);
    using var reader = new StreamReader(context.Request.Body);
    True(reader.ReadToEnd() == valid);
});
Check("Non-seekable HTTP body becomes rewindable without losing payload", () => {
    var context = Context(valid, declareLength: false);
    context.Request.Body = new NonSeekableStream(context.Request.Body);
    var target = PartnerQuota.ReadOrderTarget(context.Request, CancellationToken.None).GetAwaiter().GetResult();
    True(target.ChannelId == one && context.Request.Body.CanSeek && context.Request.Body.Position == 0);
    using var reader = new StreamReader(context.Request.Body);
    True(reader.ReadToEnd() == valid);
});
Check("Case-insensitive duplicate order scope is rejected and body rewound", () => {
    var context = Context($$"""{"partnerId":"{{partner}}","PartnerId":"{{Guid.NewGuid()}}","channelId":"{{one}}"}""");
    Reject<RequestInputException>(() => PartnerQuota.ReadOrderTarget(context.Request, CancellationToken.None).GetAwaiter().GetResult());
    True(context.Request.Body.Position == 0);
});
Check("Malformed or missing order tenant/channel is rejected", () => {
    foreach (var body in new[] { "{", "[]", "{}", $$"""{"partnerId":"{{partner}}","channelId":"bad"}""" })
        Reject<RequestInputException>(() => PartnerQuota.ReadOrderTarget(Context(body).Request, CancellationToken.None).GetAwaiter().GetResult());
});
Check("Declared oversized order is rejected before reading", () => {
    var context = Context(new string(' ', 65537));
    Reject<RequestInputException>(() => PartnerQuota.ReadOrderTarget(context.Request, CancellationToken.None).GetAwaiter().GetResult());
    True(context.Request.Body.Position == 0);
});
Check("Unknown-length oversized order is bounded and rewound", () => {
    var context = Context(new string(' ', 70000), declareLength: false);
    Reject<RequestInputException>(() => PartnerQuota.ReadOrderTarget(context.Request, CancellationToken.None).GetAwaiter().GetResult());
    True(context.Request.Body.Position == 0);
});
var output = args.FirstOrDefault(x => x.StartsWith("--report="))?[9..];
var report = JsonSerializer.Serialize(new { measuredAt = DateTimeOffset.UtcNow, scope = "Production quota planner and HTTP scope parser; no database transport or concurrency measurement", passed = results.Count - failures, failed = failures, results }, new JsonSerializerOptions { WriteIndented = true });
if (output is not null) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); File.WriteAllText(output, report); }
Console.WriteLine($"{results.Count - failures}/{results.Count} quota checks passed.");
return failures == 0 ? 0 : 1;

sealed class NonSeekableStream(Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
