using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using VortexBird.Application.Validation;
using VortexBird.Domain;
using VortexBird.Domain.Entities;
using VortexBird.Infrastructure;

namespace VortexBird.API;

public static class Telemetry
{
    public static readonly ActivitySource Activities = new("VortexBird.API");
    public static readonly Meter Meter = new("VortexBird.API", "1.0.0");
    public static readonly Counter<long> Requests = Meter.CreateCounter<long>("vortexbird.http.requests");
    public static readonly Counter<long> Errors = Meter.CreateCounter<long>("vortexbird.http.errors");
    public static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("vortexbird.http.duration", "ms");
}

public sealed class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> logger)
{
    public async Task Invoke(HttpContext ctx)
    {
        var supplied = ctx.Request.Headers["X-Correlation-Id"].FirstOrDefault();
        ctx.TraceIdentifier = supplied is not null && System.Text.RegularExpressions.Regex.IsMatch(supplied, "^[a-zA-Z0-9_-]{1,64}$") ? supplied : Guid.NewGuid().ToString("N");
        ctx.Response.Headers["X-Correlation-Id"] = ctx.TraceIdentifier;
        var watch = Stopwatch.StartNew();
        using var activity = Telemetry.Activities.StartActivity("api.request");
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = ctx.TraceIdentifier, ["TraceId"] = Activity.Current?.TraceId.ToString(), ["RequestId"] = ctx.TraceIdentifier, ["Service"] = "VortexBird.API" });
        try { await next(ctx); }
        catch (Exception ex) when (!ctx.Response.HasStarted)
        {
            var (status, code) = ex switch
            {
                AccessDeniedException => (403, "forbidden"),
                MissingEntityException => (404, "not_found"),
                RequestInputException or RequestValidationException or BadHttpRequestException => (400, "validation_error"),
                DomainException d when d.Code == "invalid_idempotency_key" => (400, d.Code),
                DomainException d => (409, d.Code),
                DbUpdateConcurrencyException => (409, "concurrency_conflict"),
                DbUpdateException => (409, "constraint_violation"),
                Npgsql.NpgsqlException => (503, "database_unavailable"),
                HttpRequestException or TimeoutException => (503, "integration_unavailable"),
                _ => (500, "internal_error")
            };
            if (status >= 500) logger.LogError(ex, "Request failed {Endpoint} {ErrorCode}", ctx.Request.Path, code);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/problem+json";
            await ctx.Response.WriteAsJsonAsync(new
            {
                type = $"urn:vortexbird:error:{code}", title = status >= 500 ? "Servicio temporalmente no disponible" : "No se pudo completar la operación",
                status, detail = status >= 500 || ex is DbUpdateException ? "Reintenta o contacta soporte con el identificador de correlación." : ex.Message,
                instance = ctx.Request.Path.Value, errorCode = code, correlationId = ctx.TraceIdentifier, timestamp = DateTimeOffset.UtcNow,
                validationErrors = (ex as RequestValidationException)?.Errors
            }, options: (System.Text.Json.JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: ctx.RequestAborted);
        }
        finally
        {
            Telemetry.Requests.Add(1, new KeyValuePair<string, object?>("method", ctx.Request.Method));
            Telemetry.Duration.Record(watch.Elapsed.TotalMilliseconds);
            if (ctx.Response.StatusCode >= 400) Telemetry.Errors.Add(1);
            logger.LogInformation("HTTP {Method} {Endpoint} {StatusCode} {Duration}ms", ctx.Request.Method, ctx.Request.Path, ctx.Response.StatusCode, watch.Elapsed.TotalMilliseconds);
            // Never persist request/response bodies, authorization headers, passwords or payment details.
            if (ctx.User.Identity?.IsAuthenticated == true && (ctx.Request.Method != "GET" || ctx.Response.StatusCode == 403))
            {
                try
                {
                    var db = ctx.RequestServices.GetRequiredService<IntegrationDbContext>();
                    db.ChangeTracker.Clear();
                    db.ActivityLogs.Add(new ActivityLog { UserId = ctx.User.FindFirst("sub")?.Value ?? "anonymous", Action = ctx.Request.Method, Module = ctx.Request.Path.Value?.Split('/').ElementAtOrDefault(3) ?? "api", EntityId = ctx.Request.RouteValues["id"]?.ToString() ?? "", Result = ctx.Response.StatusCode < 400 ? "success" : "failure", Description = $"HTTP {ctx.Response.StatusCode}", CorrelationId = ctx.TraceIdentifier, RequestId = ctx.TraceIdentifier, IpAddress = ctx.Connection.RemoteIpAddress?.ToString() ?? "", UserAgent = ctx.Request.Headers.UserAgent.ToString()[..Math.Min(500, ctx.Request.Headers.UserAgent.ToString().Length)], PartnerId = ctx.RequestServices.GetRequiredService<HttpActor>().PartnerId });
                    await db.SaveChangesAsync(CancellationToken.None);
                }
                catch (Exception auditError) { logger.LogError(auditError, "Audit write failed {CorrelationId}", ctx.TraceIdentifier); }
            }
        }
    }
}
