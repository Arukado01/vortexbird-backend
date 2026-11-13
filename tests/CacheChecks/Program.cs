using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VortexBird.Infrastructure;

var results = new List<object>();
var failures = 0;
async Task Check(string name, Func<Task> test)
{
    try { await test(); results.Add(new { name, status = "passed" }); Console.WriteLine("PASS " + name); }
    catch (Exception error)
    {
        failures++;
        // Failure reports include types only: fixtures deliberately contain fake secret markers.
        results.Add(new { name, status = "failed", error = error.GetType().Name });
        Console.Error.WriteLine("FAIL " + name + ": " + error.GetType().Name);
    }
}
void True(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
async Task<T> Reject<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T error) { return error; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
MemoryStream Wire(string text) => new(Encoding.UTF8.GetBytes(text));
byte[] Bulk(string value)
{
    var payload = Encoding.UTF8.GetBytes(value);
    return [.. Encoding.ASCII.GetBytes("$" + payload.Length + "\r\n"), .. payload, 13, 10];
}
Task RejectWire(string wire) => Reject<IOException>(async () => { using var stream = Wire(wire); await RedisResp.ReadReply(stream, default); });
Task RejectConfig(string connection, bool secure = false, string? username = null, string? password = null)
    => Reject<RedisConfigurationException>(() => { RedisEndpoint.Parse(connection, username, password, secure); return Task.CompletedTask; });

await Check("Bulk payload consumes UTF-8 bytes including emoji and accents", async () => {
    const string value = "café / 東京 / 🐦";
    using var stream = new MemoryStream(Bulk(value));
    var reply = await RedisResp.ReadReply(stream, default);
    True(reply.Kind == RedisReplyKind.Bulk && reply.Value == value && stream.Position == stream.Length);
});
await Check("Embedded CRLF and JSON whitespace survive without line truncation", async () => {
    const string value = "{\r\n  \"name\": \"Vórtex\",\n  \"stock\": 12\r\n}\n";
    using var stream = new MemoryStream(Bulk(value));
    True((await RedisResp.ReadReply(stream, default)).Value == value);
});
await Check("Consecutive replies remain aligned after exact bulk length", async () => {
    using var stream = new MemoryStream([.. Bulk("a\r\nb🦜"), .. Encoding.ASCII.GetBytes("+OK\r\n:42\r\n$-1\r\n")]);
    True((await RedisResp.ReadReply(stream, default)).Value == "a\r\nb🦜");
    True((await RedisResp.ReadReply(stream, default)).Kind == RedisReplyKind.Simple);
    var integer = await RedisResp.ReadReply(stream, default);
    True(integer.Kind == RedisReplyKind.Integer && integer.Value == "42");
    True((await RedisResp.ReadReply(stream, default)).Kind == RedisReplyKind.Null && stream.Position == stream.Length);
});
await Check("Partial stream reads preserve multibyte payload boundaries", async () => {
    using var stream = new FragmentedStream(Bulk("ñ🦜\r\n東京"));
    True((await RedisResp.ReadReply(stream, default)).Value == "ñ🦜\r\n東京");
});
await Check("Empty bulk differs from a missing cache key", async () => {
    using var stream = Wire("$0\r\n\r\n$-1\r\n");
    var empty = await RedisResp.ReadReply(stream, default);
    var missing = await RedisResp.ReadReply(stream, default);
    True(empty.Kind == RedisReplyKind.Bulk && empty.Value == "" && missing.Kind == RedisReplyKind.Null && missing.Value is null);
});
await Check("Signed RESP integer reply accepted", async () => {
    using var stream = Wire(":-12\r\n");
    var reply = await RedisResp.ReadReply(stream, default);
    True(reply.Kind == RedisReplyKind.Integer && reply.Value == "-12");
});
await Check("Invalid and oversized bulk lengths rejected before allocation", async () => {
    foreach (var wire in new[] { "$-2\r\n", "$1000001\r\n", "$2147483648\r\n", "$ 1\r\n", "$1.0\r\n", "$\r\n" }) await RejectWire(wire);
});
await Check("Truncated payload and missing or wrong delimiter rejected", async () => {
    foreach (var wire in new[] { "$4\r\nabc", "$3\r\nabc", "$3\r\nabc\r", "$3\r\nabcXX" }) await RejectWire(wire);
});
await Check("Invalid UTF-8 payload rejected without replacement characters", async () => {
    using var stream = new MemoryStream([36, 49, 13, 10, 255, 13, 10]);
    await Reject<RedisProtocolException>(() => RedisResp.ReadReply(stream, default));
});
await Check("Malformed headers and unsupported aggregate replies rejected", async () => {
    foreach (var wire in new[] { "$1\n", "$1\rX", "*2\r\n", "\r\n", ":text\r\n", ":9223372036854775808\r\n", "+O\0K\r\n" }) await RejectWire(wire);
});
await Check("Headers bounded independently from payload size", () => RejectWire("+" + new string('x', 513) + "\r\n"));
await Check("Redis error response and reply string representations redact content", async () => {
    const string marker = "FAKE_SECRET_MARKER";
    using var stream = Wire("-ERR " + marker + "\r\n");
    var error = await Reject<RedisProtocolException>(() => RedisResp.ReadReply(stream, default));
    True(!error.ToString().Contains(marker, StringComparison.Ordinal) && error.InnerException is null);
    True(!new RedisReply(RedisReplyKind.Bulk, marker).ToString().Contains(marker, StringComparison.Ordinal));
});
await Check("Command writer uses UTF-8 byte lengths and preserves multiline values", async () => {
    using var stream = new MemoryStream();
    await RedisResp.WriteCommand(stream, ["SET", "clé", "🦜\r\nx"], default);
    var expected = Encoding.UTF8.GetBytes("*3\r\n$3\r\nSET\r\n$4\r\nclé\r\n$7\r\n🦜\r\nx\r\n");
    True(stream.ToArray().SequenceEqual(expected));
});
await Check("Command writer rejects oversized or invalid text before sending", async () => {
    using var stream = new MemoryStream();
    await Reject<RedisProtocolException>(() => RedisResp.WriteCommand(stream, ["SET", "key", new string('x', 1_000_001)], default));
    True(stream.Length == 0);
    await Reject<RedisProtocolException>(() => RedisResp.WriteCommand(stream, ["SET", "key", "\ud800"], default));
    True(stream.Length == 0);
});
await Check("RESP read and write propagate caller cancellation", async () => {
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    using var stream = Wire("+OK\r\n");
    await Reject<OperationCanceledException>(() => RedisResp.ReadReply(stream, cancelled.Token));
    await Reject<OperationCanceledException>(() => RedisResp.WriteCommand(stream, ["GET", "key"], cancelled.Token));
});
await Check("ACL authentication and database selection finish before the cache command", async () => {
    using var stream = new ReplayStream([.. Encoding.ASCII.GetBytes("+OK\r\n+OK\r\n"), .. Bulk("cached value")]);
    var endpoint = RedisEndpoint.Parse("rediss://user:password@redis.example/2", null, null, true);
    True(await ReadCache.Execute(stream, endpoint, ["GET", "key"], default) == "cached value");
    using var expected = new MemoryStream();
    await RedisResp.WriteCommand(expected, ["AUTH", "user", "password"], default);
    await RedisResp.WriteCommand(expected, ["SELECT", "2"], default);
    await RedisResp.WriteCommand(expected, ["GET", "key"], default);
    True(stream.Written.SequenceEqual(expected.ToArray()));
});
await Check("Rejected authentication never sends the requested cache operation", async () => {
    using var stream = new ReplayStream(Encoding.ASCII.GetBytes("-WRONGPASS FAKE_SECRET_MARKER\r\n"));
    var endpoint = RedisEndpoint.Parse("rediss://:password@redis.example", null, null, true);
    var error = await Reject<RedisProtocolException>(() => ReadCache.Execute(stream, endpoint, ["GET", "key"], default));
    using var expected = new MemoryStream();
    await RedisResp.WriteCommand(expected, ["AUTH", "password"], default);
    True(stream.Written.SequenceEqual(expected.ToArray()) && !error.ToString().Contains("FAKE_SECRET_MARKER", StringComparison.Ordinal));
});
await Check("Unexpected reply types cannot masquerade as cache data or successful writes", async () => {
    var endpoint = RedisEndpoint.Parse("redis:6379", null, null, false);
    using var get = new ReplayStream(Encoding.ASCII.GetBytes("+OK\r\n"));
    using var set = new ReplayStream(Encoding.ASCII.GetBytes(":1\r\n"));
    using var delete = new ReplayStream(Encoding.ASCII.GetBytes("+OK\r\n"));
    await Reject<RedisProtocolException>(() => ReadCache.Execute(get, endpoint, ["GET", "key"], default));
    await Reject<RedisProtocolException>(() => ReadCache.Execute(set, endpoint, ["SET", "key", "value"], default));
    await Reject<RedisProtocolException>(() => ReadCache.Execute(delete, endpoint, ["DEL", "key"], default));
});
await Check("Compose hostname and explicit port accepted in Development", () => {
    var endpoint = RedisEndpoint.Parse("redis:6379", null, null, false);
    True(endpoint.Host == "redis" && endpoint.Port == 6379 && !endpoint.UseTls && endpoint.Password is null);
    True(RedisEndpoint.Parse("localhost", null, null, false).Port == 6379);
    return Task.CompletedTask;
});
await Check("Bracketed IPv6 host and custom port accepted", () => {
    var endpoint = RedisEndpoint.Parse("[::1]:6380", null, null, false);
    True(endpoint.Host == "::1" && endpoint.Port == 6380);
    return Task.CompletedTask;
});
await Check("Secure URI preserves TLS and percent-decoded ACL credentials", () => {
    var endpoint = RedisEndpoint.Parse("rediss://user%40team:p%3Aa%2Fss%23@redis.example:6380/2", null, null, true);
    True(endpoint.UseTls && endpoint.Host == "redis.example" && endpoint.Port == 6380 && endpoint.Database == 2);
    True(endpoint.Username == "user@team" && endpoint.Password == "p:a/ss#");
    return Task.CompletedTask;
});
await Check("Default-user AUTH accepts empty user and password-only URI", () => {
    var emptyUser = RedisEndpoint.Parse("rediss://:password@redis.example", null, null, true);
    var passwordOnly = RedisEndpoint.Parse("rediss://password@redis.example", null, null, true);
    True(emptyUser.Username is null && passwordOnly.Username is null && emptyUser.Password == "password" && passwordOnly.Password == "password");
    return Task.CompletedTask;
});
await Check("Separate secret configuration enables secure ACL authentication", () => {
    var endpoint = RedisEndpoint.Parse("rediss://redis.example", "cache-reader", "separate-password", true);
    True(endpoint.UseTls && endpoint.Username == "cache-reader" && endpoint.Password == "separate-password");
    return Task.CompletedTask;
});
await Check("Production rejects plaintext even when AUTH credentials exist", async () => {
    await RejectConfig("redis:6379", true);
    await RejectConfig("redis://:password@redis.example", true);
    await RejectConfig("redis.example:6379", true, password: "password");
});
await Check("Production requires credentials and rejects user without password", async () => {
    await RejectConfig("rediss://redis.example", true);
    await RejectConfig("rediss://user:@redis.example", true);
    await RejectConfig("rediss://redis.example", false, username: "user");
});
await Check("Downgrade flags and alternate schemes are rejected", async () => {
    foreach (var uri in new[] { "rediss://:password@redis.example?ssl=false", "rediss://:password@redis.example#tls=false", "http://redis.example", "file:///redis" }) await RejectConfig(uri, true);
});
await Check("Invalid authority, port, database and ambiguous credentials rejected", async () => {
    foreach (var connection in new[] { "", " redis:6379", "redis:0", "redis:65536", "redis:bad", "rediss://", "redis://host/-1", "redis://host/one", "redis://host/1/2", "user:password@host" }) await RejectConfig(connection);
    await RejectConfig("rediss://:uri-password@redis.example", true, password: "other-password");
});
await Check("Configuration errors and endpoint string never reveal credentials", async () => {
    const string marker = "FAKE_SECRET_MARKER";
    var endpoint = RedisEndpoint.Parse("rediss://:" + marker + "@redis.example", null, null, true);
    True(!endpoint.ToString().Contains(marker, StringComparison.Ordinal));
    var error = await Reject<RedisConfigurationException>(() => { RedisEndpoint.Parse("rediss://:" + marker + "@redis.example:invalid", null, null, true); return Task.CompletedTask; });
    True(!error.ToString().Contains(marker, StringComparison.Ordinal) && error.InnerException is null);
});
await Check("Insecure production configuration falls back without exposing secrets", async () => {
    const string marker = "FAKE_SECRET_MARKER";
    var logger = new CapturingLogger();
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = "redis://:" + marker + "@redis.example" }).Build();
    var cache = new ReadCache(configuration, logger, new TestEnvironment("Production"));
    True(await cache.Get<string>("key", default) is null);
    await cache.Set("key", "value", 30, default);
    await cache.Invalidate("key", default);
    True(logger.Messages.Count == 3 && logger.Messages.All(message => !message.Contains(marker, StringComparison.Ordinal)) && !logger.HasException);
});
await Check("Disabled cache returns a miss without logging or connecting", async () => {
    var logger = new CapturingLogger();
    var cache = new ReadCache(new ConfigurationBuilder().Build(), logger, new TestEnvironment("Production"));
    True(await cache.Get<string>("key", default) is null);
    await cache.Set("key", "value", 30, default);
    await cache.Invalidate("key", default);
    True(logger.Messages.Count == 0);
});
await Check("Caller cancellation is never converted to an authoritative cache miss", async () => {
    var cache = new ReadCache(new ConfigurationBuilder().Build(), new CapturingLogger(), new TestEnvironment("Production"));
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    await Reject<OperationCanceledException>(() => cache.Get<string>("key", cancelled.Token));
    await Reject<OperationCanceledException>(() => cache.Set("key", "value", 30, cancelled.Token));
    await Reject<OperationCanceledException>(() => cache.Invalidate("key", cancelled.Token));
});
await Check("Malformed timeout setting falls back before any network request", async () => {
    var logger = new CapturingLogger();
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["ConnectionStrings:Redis"] = "rediss://redis.example", ["Redis:Password"] = "FAKE_SECRET_MARKER", ["Redis:TimeoutMilliseconds"] = "invalid"
    }).Build();
    var cache = new ReadCache(configuration, logger, new TestEnvironment("Production"));
    True(await cache.Get<string>("key", default) is null && logger.Messages.Count == 1 && !logger.HasException);
});

Console.WriteLine($"{results.Count - failures}/{results.Count} checks passed.");
var report = args.FirstOrDefault(arg => arg.StartsWith("--report=", StringComparison.Ordinal))?[9..];
if (report is not null)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(report));
    if (directory is not null) Directory.CreateDirectory(directory);
    await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { suite = "CacheChecks", passed = results.Count - failures, failed = failures, checks = results }, new JsonSerializerOptions { WriteIndented = true }));
}
return failures == 0 ? 0 : 1;

sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], cancellationToken);
}
sealed class ReplayStream(byte[] replies) : Stream
{
    private readonly MemoryStream response = new(replies);
    private readonly MemoryStream request = new();
    public byte[] Written => request.ToArray();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => response.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => request.Write(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => response.ReadAsync(buffer, cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => request.WriteAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) { response.Dispose(); request.Dispose(); } base.Dispose(disposing); }
}
sealed class CapturingLogger : ILogger<ReadCache>
{
    public List<string> Messages { get; } = [];
    public bool HasException { get; private set; }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        HasException |= exception is not null;
        Messages.Add(formatter(state, exception));
    }
}
sealed class TestEnvironment(string name) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;
    public string ApplicationName { get; set; } = "CacheChecks";
    public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
