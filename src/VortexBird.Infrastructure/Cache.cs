using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VortexBird.Infrastructure;

// Small RESP2 client for GET/SET/DEL. It degrades to authoritative PostgreSQL reads.
public sealed class ReadCache(IConfiguration config, ILogger<ReadCache> logger)
{
    public async Task<T?> Get<T>(string key, CancellationToken ct) where T : class
    {
        var value = await Command(["GET", key], ct);
        return value is null ? null : JsonSerializer.Deserialize<T>(value);
    }
    public async Task Set<T>(string key, T value, int seconds, CancellationToken ct) => await Command(["SET", key, JsonSerializer.Serialize(value), "EX", seconds.ToString()], ct);
    public async Task Invalidate(string key, CancellationToken ct) => await Command(["DEL", key], ct);
    private async Task<string?> Command(string[] args, CancellationToken ct)
    {
        var connection = config.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(connection)) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(150);
        try
        {
            var parts = connection.Split(':');
            using var client = new TcpClient();
            await client.ConnectAsync(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6379, timeout.Token);
            await using var stream = client.GetStream();
            var request = "*" + args.Length + "\r\n" + string.Concat(args.Select(a => "$" + Encoding.UTF8.GetByteCount(a) + "\r\n" + a + "\r\n"));
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request), timeout.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var header = await reader.ReadLineAsync(timeout.Token);
            if (header is null || header == "$-1" || !header.StartsWith('$')) return null;
            var length = int.Parse(header[1..]);
            if (length is < 0 or > 1000000) return null;
            return await reader.ReadLineAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or FormatException)
        {
            if (ct.IsCancellationRequested) throw;
            logger.LogDebug("Redis unavailable; authoritative query fallback ({Error})", ex.GetType().Name);
            return null;
        }
    }
}

public sealed class AdapterCircuit
{
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset Until)> state = new();
    public void Check(string system)
    {
        if (state.TryGetValue(system, out var value) && value.Until > DateTimeOffset.UtcNow) throw new HttpRequestException("Circuito abierto: " + system);
    }
    public void Success(string system) => state.TryRemove(system, out _);
    public void Fail(string system) => state.AddOrUpdate(system, (1, DateTimeOffset.MinValue), (_, old) => (old.Failures + 1, old.Failures >= 2 ? DateTimeOffset.UtcNow.AddSeconds(30) : DateTimeOffset.MinValue));
}
