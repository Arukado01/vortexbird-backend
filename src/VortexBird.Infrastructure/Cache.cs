using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VortexBird.Infrastructure;

// Small RESP2 client for GET/SET/DEL. It degrades to authoritative PostgreSQL reads.
public sealed class ReadCache(IConfiguration config, ILogger<ReadCache> logger, IHostEnvironment environment)
{
    public async Task<T?> Get<T>(string key, CancellationToken ct) where T : class
    {
        var value = await Command(["GET", key], ct);
        if (value is null) return null;
        try { return JsonSerializer.Deserialize<T>(value); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            LogFallback(ex);
            return null;
        }
    }
    public async Task Set<T>(string key, T value, int seconds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (seconds <= 0) return;
        try { await Command(["SET", key, JsonSerializer.Serialize(value), "EX", seconds.ToString(CultureInfo.InvariantCulture)], ct); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { LogFallback(ex); }
    }
    public async Task Invalidate(string key, CancellationToken ct) => await Command(["DEL", key], ct);
    private async Task<string?> Command(string[] args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var connection = config.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(connection)) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            // Plaintext is intentionally limited to local Development (Docker Compose).
            var endpoint = RedisEndpoint.Parse(connection, config["Redis:Username"], config["Redis:Password"], !environment.IsDevelopment());
            var timeoutText = config["Redis:TimeoutMilliseconds"];
            var timeoutMilliseconds = 1000;
            if (timeoutText is not null && (!int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out timeoutMilliseconds) || timeoutMilliseconds is < 50 or > 5000))
                throw new RedisConfigurationException();
            timeout.CancelAfter(timeoutMilliseconds);
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token);
            await using var network = client.GetStream();
            if (endpoint.UseTls)
            {
                // Default certificate chain and hostname validation. Never retry over plaintext.
                await using var tls = new SslStream(network, leaveInnerStreamOpen: true);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = endpoint.Host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.Online
                }, timeout.Token);
                return await Execute(tls, endpoint, args, timeout.Token);
            }
            return await Execute(network, endpoint, args, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or AuthenticationException or RedisConfigurationException)
        {
            ct.ThrowIfCancellationRequested();
            LogFallback(ex);
            return null;
        }
    }

    internal static async Task<string?> Execute(Stream stream, RedisEndpoint endpoint, string[] args, CancellationToken ct)
    {
        if (endpoint.Password is not null)
        {
            string[] auth = endpoint.Username is null ? ["AUTH", endpoint.Password] : ["AUTH", endpoint.Username, endpoint.Password];
            await RedisResp.WriteCommand(stream, auth, ct);
            RequireOk(await RedisResp.ReadReply(stream, ct));
        }
        if (endpoint.Database != 0)
        {
            await RedisResp.WriteCommand(stream, ["SELECT", endpoint.Database.ToString(CultureInfo.InvariantCulture)], ct);
            RequireOk(await RedisResp.ReadReply(stream, ct));
        }
        await RedisResp.WriteCommand(stream, args, ct);
        var reply = await RedisResp.ReadReply(stream, ct);
        if (args[0] == "GET")
        {
            if (reply.Kind is not (RedisReplyKind.Bulk or RedisReplyKind.Null)) throw new RedisProtocolException();
            return reply.Value;
        }
        if (args[0] == "SET") RequireOk(reply);
        else if (reply.Kind != RedisReplyKind.Integer) throw new RedisProtocolException();
        return null;
    }

    private static void RequireOk(RedisReply reply)
    {
        if (reply.Kind != RedisReplyKind.Simple || reply.Value != "OK") throw new RedisProtocolException();
    }

    // Never include connection strings, exceptions, server error text, keys, or cache values.
    private void LogFallback(Exception ex) => logger.LogDebug("Redis unavailable; authoritative query fallback ({Error})", ex.GetType().Name);
}

internal sealed class RedisConfigurationException() : Exception("Invalid Redis configuration.");
internal sealed class RedisProtocolException() : IOException("Invalid Redis response.");

internal sealed class RedisEndpoint
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required bool UseTls { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public int Database { get; init; }
    public override string ToString() => "Redis endpoint (redacted)";

    public static RedisEndpoint Parse(string connection, string? username, string? password, bool requireSecure)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(connection) || connection != connection.Trim()) throw new RedisConfigurationException();
            var explicitUri = connection.Contains("://", StringComparison.Ordinal);
            if (!explicitUri && connection.IndexOfAny(['@', '/', '?', '#']) >= 0) throw new RedisConfigurationException();
            if (!Uri.TryCreate(explicitUri ? connection : "redis://" + connection, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("redis" or "rediss") || uri.HostNameType == UriHostNameType.Unknown
                || string.IsNullOrEmpty(uri.Host) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new RedisConfigurationException();
            var port = uri.Port == -1 ? 6379 : uri.Port;
            if (port is < 1 or > 65535) throw new RedisConfigurationException();
            var database = 0;
            if (uri.AbsolutePath is not ("" or "/") && (!int.TryParse(uri.AbsolutePath.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out database) || database < 0))
                throw new RedisConfigurationException();
            if (uri.UserInfo.Length > 0)
            {
                if (username is not null || password is not null) throw new RedisConfigurationException();
                var separator = uri.UserInfo.IndexOf(':');
                username = separator < 0 ? null : Uri.UnescapeDataString(uri.UserInfo[..separator]);
                password = Uri.UnescapeDataString(separator < 0 ? uri.UserInfo : uri.UserInfo[(separator + 1)..]);
            }
            if (username == "") username = null;
            if (password == "") password = null;
            var tls = uri.Scheme == "rediss";
            if ((username is not null && password is null) || (requireSecure && (!tls || password is null))) throw new RedisConfigurationException();
            return new RedisEndpoint { Host = uri.DnsSafeHost, Port = port, UseTls = tls, Username = username, Password = password, Database = database };
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            // Do not preserve inner exceptions: URI errors may contain credentials.
            throw new RedisConfigurationException();
        }
    }
}

internal enum RedisReplyKind { Null, Bulk, Simple, Integer }
internal readonly struct RedisReply(RedisReplyKind kind, string? value)
{
    public RedisReplyKind Kind { get; } = kind;
    public string? Value { get; } = value;
    public override string ToString() => "Redis reply (redacted)";
}

internal static class RedisResp
{
    private const int MaximumBulkBytes = 1_000_000;
    private const int MaximumHeaderBytes = 512;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly byte[] CrLf = [13, 10];

    public static async Task WriteCommand(Stream stream, string[] args, CancellationToken ct)
    {
        if (args.Length is < 1 or > 16) throw new RedisProtocolException();
        try
        {
            // Pre-validate lengths before writing any partial command.
            if (args.Any(arg => Utf8.GetByteCount(arg) > MaximumBulkBytes)) throw new RedisProtocolException();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("*" + args.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"), ct);
            foreach (var arg in args)
            {
                var bytes = Utf8.GetBytes(arg);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("$" + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"), ct);
                await stream.WriteAsync(bytes, ct);
                await stream.WriteAsync(CrLf, ct);
            }
        }
        catch (EncoderFallbackException) { throw new RedisProtocolException(); }
    }

    public static async Task<RedisReply> ReadReply(Stream stream, CancellationToken ct)
    {
        var header = await ReadHeader(stream, ct);
        if (header.Length == 0) throw new RedisProtocolException();
        if (header[0] == '-') throw new RedisProtocolException(); // Server errors can contain secrets.
        if (header[0] == '+') return new RedisReply(RedisReplyKind.Simple, header[1..]);
        if (header[0] == ':')
        {
            if (!long.TryParse(header.AsSpan(1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) throw new RedisProtocolException();
            return new RedisReply(RedisReplyKind.Integer, header[1..]);
        }
        if (header[0] != '$' || !int.TryParse(header.AsSpan(1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var length)) throw new RedisProtocolException();
        if (length == -1) return new RedisReply(RedisReplyKind.Null, null);
        if (length is < 0 or > MaximumBulkBytes) throw new RedisProtocolException();
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct);
        var delimiter = new byte[2];
        await stream.ReadExactlyAsync(delimiter, ct);
        if (delimiter[0] != 13 || delimiter[1] != 10) throw new RedisProtocolException();
        try { return new RedisReply(RedisReplyKind.Bulk, Utf8.GetString(bytes)); }
        catch (DecoderFallbackException) { throw new RedisProtocolException(); }
    }

    private static async Task<string> ReadHeader(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaximumHeaderBytes];
        var single = new byte[1];
        var count = 0;
        while (true)
        {
            await stream.ReadExactlyAsync(single, ct);
            if (single[0] == 13)
            {
                await stream.ReadExactlyAsync(single, ct);
                if (single[0] != 10) throw new RedisProtocolException();
                return Encoding.ASCII.GetString(buffer, 0, count);
            }
            if (single[0] is < 32 or > 126 || count == MaximumHeaderBytes) throw new RedisProtocolException();
            buffer[count++] = single[0];
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
