using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using VortexBird.API;
using VortexBird.Application.Abstractions;
using VortexBird.Infrastructure;
using VortexBird.Infrastructure.Payments;
using VortexBird.Infrastructure.Messaging;
using VortexBird.Infrastructure.Deployment;

var railwayPortSetting = Environment.GetEnvironmentVariable("PORT");
int? railwayPort = null;
if (!string.IsNullOrEmpty(railwayPortSetting))
{
    if (!int.TryParse(railwayPortSetting, out var port) || port is < 1 or > 65_535)
        throw new InvalidOperationException("PORT must be a valid TCP port.");
    railwayPort = port;
}

if (args is ["--readiness-probe"])
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        using var result = await client.GetAsync($"http://127.0.0.1:{railwayPort ?? 8080}/health/ready");
        Environment.ExitCode = result.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
    {
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Any(argument => argument is "--migrate" or "--bootstrap-admin" or "--bind-pilot-roles"))
{
    Environment.ExitCode = await DeploymentJob.Run(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);
if (railwayPort is { } listenPort) builder.WebHost.UseUrls($"http://*:{listenPort}");
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT"
        };
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, _) =>
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any())
        {
            operation.Security = [new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = []
            }];
        }
        return Task.CompletedTask;
    });
});
builder.Services.AddHttpContextAccessor();
var databaseConnection = DatabaseConnectionPolicy.Validate(DatabaseConnectionPolicy.ReadConfiguredConnection(builder.Configuration)
    ?? "Host=localhost;Port=5432;Database=vortexbird;Username=vortexbird;Timeout=3;Command Timeout=10", builder.Environment.IsDevelopment());
builder.Services.AddDbContext<IntegrationDbContext>(o => o.UseNpgsql(databaseConnection));
builder.Services.AddScoped<HttpActor>();
builder.Services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<HttpActor>());
builder.Services.AddScoped<CreateOrderHandler>();
builder.Services.AddScoped<GetOrderHandler>();
builder.Services.AddScoped<OrderSaga>();
builder.Services.AddScoped<SimulatorGuard>();
builder.Services.AddScoped<IPricingAdapter, ErpPricingAdapter>();
builder.Services.AddScoped<IInventoryAdapter, WmsInventoryAdapter>();
builder.Services.AddScoped<ICustomerAdapter, CrmCustomerAdapter>();
builder.Services.AddScoped<IOrderManagementAdapter, OmsOrderAdapter>();
builder.Services.AddScoped<IPaymentGateway, SimulatedPaymentGateway>();
builder.Services.AddPaymentProcessing(builder.Configuration);
builder.Services.AddSingleton<ReadCache>();
builder.Services.AddSingleton<AdapterCircuit>();
builder.Services.AddScoped<PartnerQuota>();
builder.Services.AddScoped<IQuotaStore, PostgresQuotaStore>();
builder.Services.AddIntegrationMessaging(builder.Configuration, builder.Environment);
builder.Services.AddAuthorization();
var demo = builder.Configuration.GetValue<bool>("Authentication:DemoEnabled");
var cognito = CognitoAuthentication.Read(builder.Configuration);
var simulationEnabled = OrderSimulationEndpoints.IsEnabled(builder.Environment, builder.Configuration);
if (demo && !builder.Environment.IsDevelopment()) throw new InvalidOperationException("Demo authentication is forbidden outside Development.");
if (demo && cognito is not null) throw new InvalidOperationException("Demo authentication cannot be enabled with Cognito.");
var demoKey = builder.Configuration["Authentication:DemoSigningKey"];
if (demo && (string.IsNullOrWhiteSpace(demoKey) || demoKey.Length < 32)) throw new InvalidOperationException("Development demo requires Authentication:DemoSigningKey with at least 32 characters.");
var auth = builder.Services.AddAuthentication(o => { o.DefaultAuthenticateScheme = "router"; o.DefaultChallengeScheme = "router"; })
    .AddPolicyScheme("router", "Validated issuer router", o => o.ForwardDefaultSelector = ctx =>
    {
        if (demo)
        {
            var token = ctx.Request.Headers.Authorization.ToString();
            if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                try { if (new JwtSecurityTokenHandler().ReadJwtToken(token[7..]).Issuer == "vortexbird-development") return "demo"; } catch (ArgumentException) { }
        }
        return "oidc";
    })
    .AddJwtBearer("oidc", o =>
    {
        if (cognito is not null)
        {
            cognito.Configure(o);
            return;
        }
        o.Authority = builder.Configuration["Authentication:Authority"] ?? "http://localhost:8080/realms/vortexbird";
        o.Audience = builder.Configuration["Authentication:Audience"] ?? "vortexbird-api";
        o.RequireHttpsMetadata = !builder.Environment.IsDevelopment() || builder.Configuration.GetValue("Authentication:RequireHttpsMetadata", false);
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = builder.Configuration["Authentication:ValidIssuer"] ?? o.Authority, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true, ClockSkew = TimeSpan.FromSeconds(30), NameClaimType = "sub" };
    });
if (demo) auth.AddJwtBearer("demo", o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = "vortexbird-development", ValidateAudience = true, ValidAudience = "vortexbird-api", ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(demoKey!)), ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(15), ValidAlgorithms = [SecurityAlgorithms.HmacSha256] };
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173"]).WithHeaders("Authorization", "Content-Type", "Idempotency-Key", "X-Correlation-Id").WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS").WithExposedHeaders("X-Correlation-Id", "Retry-After")));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    // The authenticated ceiling matches the maximum configurable partner quota;
    // the database-driven quota below remains the effective tenant policy.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetFixedWindowLimiter((ctx.Request.Path.StartsWithSegments("/api/v1/auth/demo-login") ? "login:" : "api:") + (ctx.User.FindFirstValue("partner_id") ?? ctx.User.FindFirstValue("sub") ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"), _ => new FixedWindowRateLimiterOptions { PermitLimit = ctx.Request.Path.StartsWithSegments("/api/v1/auth/demo-login") ? 10 : ctx.User.Identity?.IsAuthenticated == true ? 100000 : 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    o.OnRejected = async (context, ct) => { context.HttpContext.Response.Headers.RetryAfter = "60"; await context.HttpContext.Response.WriteAsJsonAsync(new { type = "urn:vortexbird:error:rate_limit", title = "Límite de solicitudes excedido", status = 429, detail = "Reintenta en 60 segundos.", errorCode = "rate_limit", correlationId = context.HttpContext.TraceIdentifier, timestamp = DateTimeOffset.UtcNow }, cancellationToken: ct); };
});
var app = builder.Build();
app.UseMiddleware<ErrorMiddleware>();
app.UseStatusCodePages(async (Microsoft.AspNetCore.Diagnostics.StatusCodeContext context) =>
{
    var ctx = context.HttpContext;
    await ctx.Response.WriteAsJsonAsync(new { type = $"urn:vortexbird:http:{ctx.Response.StatusCode}", title = ctx.Response.StatusCode == 401 ? "Autenticación requerida" : "Solicitud no completada", status = ctx.Response.StatusCode, detail = "Verifica tus credenciales y permisos.", instance = ctx.Request.Path.Value, errorCode = $"http_{ctx.Response.StatusCode}", correlationId = ctx.TraceIdentifier, timestamp = DateTimeOffset.UtcNow }, options: (System.Text.Json.JsonSerializerOptions?)null, contentType: "application/problem+json");
});
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (ctx.Request.Path.StartsWithSegments("/api") && ctx.User.Identity?.IsAuthenticated == true)
    {
        var actor = ctx.RequestServices.GetRequiredService<HttpActor>();
        await actor.Initialize(ctx.RequestAborted);
        var quota = ctx.RequestServices.GetRequiredService<PartnerQuota>();
        var decision = await quota.TryAcquire(ctx, actor, ctx.RequestAborted);
        if (!decision.Allowed)
        {
            ctx.Response.StatusCode = 429; ctx.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await ctx.Response.WriteAsJsonAsync(new { type = "urn:vortexbird:error:partner_quota", title = "Cuota de partner/canal agotada", status = 429, detail = "Reintenta en la siguiente ventana.", errorCode = "partner_quota", correlationId = ctx.TraceIdentifier, timestamp = DateTimeOffset.UtcNow }, options: (System.Text.Json.JsonSerializerOptions?)null, contentType: "application/problem+json");
            return;
        }
        string[] partnerWideResources = ["/api/v1/webhooks", "/api/v1/webhook-deliveries", "/api/v1/users", "/api/v1/events", "/api/v1/activity-logs"];
        if (partnerWideResources.Any(prefix => ctx.Request.Path.StartsWithSegments(prefix)) || ctx.Request.Path == "/api/v1/channels" && ctx.Request.Method == "POST") actor.RequireUnrestrictedChannel();
    }
    await next(ctx);
});
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "VortexBird API v1"));
}
app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "VortexBird.API" }));
app.MapGet("/health/ready", async (IntegrationDbContext db, CancellationToken ct) =>
{
    try
    {
        // A listening database is not sufficient when a deployment has not applied its schema.
        var ready = await db.Database.CanConnectAsync(ct)
            && !(await db.Database.GetPendingMigrationsAsync(ct)).Any();
        return ready ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
    }
    catch (System.Data.Common.DbException)
    {
        // The connection can fail between the reachability and migration-history checks.
        return Results.StatusCode(503);
    }
});
app.MapGet("/api/v1/auth/config", () => cognito?.PublicConfiguration(simulationEnabled) ?? new
{
    demoEnabled = demo, simulationEnabled, authority = builder.Configuration["Authentication:PublicAuthority"] ??
        builder.Configuration["Authentication:ValidIssuer"] ?? builder.Configuration["Authentication:Authority"] ?? "http://localhost:8080/realms/vortexbird",
    clientId = "vortexbird-portal"
});
if (demo)
{
    app.MapPost("/api/v1/auth/demo-login", async (DemoLogin input, IntegrationDbContext db, IConfiguration config) =>
    {
        var password = config["Demo:Password"];
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12) return Results.Problem("Demo password is not configured.", statusCode: 503);
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        var provided = SHA256.HashData(Encoding.UTF8.GetBytes(input.Password ?? ""));
        if (!CryptographicOperations.FixedTimeEquals(expected, provided) || !string.Equals(input.Email, config["Demo:Email"] ?? "admin@andesretail.local", StringComparison.OrdinalIgnoreCase)) return Results.Unauthorized();
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == DemoSeed.AdminId && x.Enabled);
        if (user is null) return Results.Unauthorized();
        var permissions = await (from ur in db.UserRoles join rp in db.RolePermissions on ur.RoleId equals rp.RoleId join p in db.Permissions on rp.PermissionId equals p.Id where ur.UserId == user.Id select p.Name).Distinct().ToListAsync();
        var expires = DateTime.UtcNow.AddMinutes(10);
        var token = new JwtSecurityToken("vortexbird-development", "vortexbird-api", [new Claim("sub", user.Subject), new Claim("email", user.Email), new Claim("identity_type", "human")], expires: expires, signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(demoKey!)), SecurityAlgorithms.HmacSha256));
        return Results.Ok(new { accessToken = new JwtSecurityTokenHandler().WriteToken(token), user = new { user.Id, user.Email, user.DisplayName, user.PartnerId }, permissions, expiresAt = expires });
    });
}
var api = app.MapGroup("/api/v1").RequireAuthorization();
api.MapGet("/auth/me", (HttpActor actor) => new { user = actor.User, permissions = actor.Permissions });
api.MapOperationalEndpoints();
if (simulationEnabled) api.MapOrderSimulationEndpoints();
api.MapAdministrationEndpoints();
api.MapDashboardEndpoints();
api.MapPaymentEndpoints();
app.MapMercadoPagoWebhook();
if (builder.Configuration.GetValue<bool>("Database:Initialize"))
{
    if (!app.Environment.IsDevelopment()) throw new InvalidOperationException("Automatic schema bootstrap is Development only. Run reviewed migrations in deployment.");
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
    await db.Database.MigrateAsync();
    if (builder.Configuration.GetValue<bool>("Seed:Enabled")) await DemoSeed.Run(db, builder.Configuration);
}
app.Run();
public record DemoLogin(string Email, string Password);
public partial class Program;
