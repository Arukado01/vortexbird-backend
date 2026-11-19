using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace VortexBird.API;

internal sealed record CognitoAuthentication(string Issuer, string ClientId, string AuthorizationOrigin, string RequiredScope)
{
    public static CognitoAuthentication? Read(IConfiguration configuration)
    {
        var provider = configuration["Authentication:Provider"];
        if (string.IsNullOrWhiteSpace(provider) || provider.Equals("Keycloak", StringComparison.OrdinalIgnoreCase)) return null;
        if (!provider.Equals("Cognito", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Authentication:Provider must be Keycloak or Cognito.");

        var issuer = configuration["Authentication:Authority"]?.TrimEnd('/') ?? "";
        var clientId = configuration["Authentication:Cognito:ClientId"] ?? "";
        var origin = configuration["Authentication:Cognito:AuthorizationOrigin"];
        var scope = configuration["Authentication:Cognito:RequiredScope"] ?? "vortexbird-api/access";
        if (!IsUserPoolIssuer(issuer) ||
            !Regex.IsMatch(clientId, "^[a-z0-9]{8,128}$", RegexOptions.CultureInvariant) ||
            !Uri.TryCreate(origin, UriKind.Absolute, out var authorizationUri) ||
            authorizationUri.Scheme != Uri.UriSchemeHttps || authorizationUri.HostNameType != UriHostNameType.Dns ||
            !authorizationUri.IsDefaultPort || authorizationUri.UserInfo.Length != 0 || authorizationUri.AbsolutePath != "/" ||
            authorizationUri.Query.Length != 0 || authorizationUri.Fragment.Length != 0 ||
            !Regex.IsMatch(scope, "^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException("Configure the Cognito User Pool issuer, app client, HTTPS login origin and API scope.");
        }

        return new(issuer, clientId, authorizationUri.GetLeftPart(UriPartial.Authority), scope);
    }

    public static bool IsUserPoolIssuer(string issuer) => Regex.IsMatch(issuer,
        @"^https://cognito-idp\.(?<region>[a-z]{2}(?:-[a-z]+)+-[0-9]+)\.amazonaws\.com/\k<region>_[A-Za-z0-9]+$",
        RegexOptions.CultureInvariant);

    public object PublicConfiguration(bool simulationEnabled) => new
    {
        provider = "cognito", authority = Issuer, clientId = ClientId, authorizationOrigin = AuthorizationOrigin,
        scope = $"openid email profile {RequiredScope}", demoEnabled = false, simulationEnabled
    };

    public void Configure(JwtBearerOptions options)
    {
        options.Authority = Issuer;
        options.RequireHttpsMetadata = true;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = Issuer, RequireSignedTokens = true, ValidateIssuerSigningKey = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256], RequireExpirationTime = true, ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30), NameClaimType = "sub",
            // Cognito access tokens name the app client in client_id. Check it below after cryptographic validation.
            ValidateAudience = false
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                if (context.Principal is not { } principal || !TryApplyHumanIdentity(principal))
                    context.Fail("The Cognito token does not match the VortexBird app client and API scope.");
                return Task.CompletedTask;
            }
        };
    }

    private bool TryApplyHumanIdentity(ClaimsPrincipal principal)
    {
        bool HasOneClaim(string name, string value) => principal.FindAll(name).Select(claim => claim.Value).SequenceEqual([value]);
        var explicitlyGranted = principal.FindAll("scope").SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!HasOneClaim("iss", Issuer) || !HasOneClaim("token_use", "access") || !HasOneClaim("client_id", ClientId) ||
            !explicitlyGranted.Contains(RequiredScope, StringComparer.Ordinal) ||
            principal.FindAll("sub").Count() != 1 || !Guid.TryParseExact(principal.FindFirstValue("sub"), "D", out var subject) ||
            subject == Guid.Empty || principal.Identity is not ClaimsIdentity identity)
        {
            return false;
        }

        // App permissions and partner associations come from the existing PostgreSQL account binding.
        foreach (var claim in identity.Claims.Where(claim => claim.Type is "identity_type" or "partner_id" or "channel_id").ToArray())
            identity.RemoveClaim(claim);
        identity.AddClaim(new Claim("identity_type", "human"));
        return true;
    }
}
