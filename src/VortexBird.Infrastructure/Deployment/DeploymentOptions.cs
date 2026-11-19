using System.Net.Mail;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace VortexBird.Infrastructure.Deployment;

public sealed class DeploymentConfigurationException(string message) : Exception(message);
public sealed class DeploymentPolicyException(string message) : Exception(message);

public sealed record InitialAdministrator(string Subject, string Email, string DisplayName, string Issuer)
{
    public static InitialAdministrator Read(IConfiguration config)
    {
        var subject = Required(config, "Bootstrap:AdminSubject", 200);
        if (subject == "demo-admin" || subject.Any(char.IsControl))
            throw new DeploymentConfigurationException("Bootstrap requires the real OIDC subject, not the demonstration identity.");
        var email = Required(config, "Bootstrap:AdminEmail", 254).ToLowerInvariant();
        if (!MailAddress.TryCreate(email, out var address) || address.Address != email)
            throw new DeploymentConfigurationException("Bootstrap:AdminEmail must be a valid email address.");
        var displayName = Required(config, "Bootstrap:AdminDisplayName", 150);
        var issuer = Required(config, "Bootstrap:OidcIssuer", 2048).TrimEnd('/');
        var configuredIssuer = (config["Authentication:ValidIssuer"] ?? config["Authentication:Authority"])?.TrimEnd('/');
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !string.Equals(issuer, configuredIssuer, StringComparison.Ordinal))
            throw new DeploymentConfigurationException("Bootstrap requires an HTTPS issuer matching Authentication:ValidIssuer or Authentication:Authority.");
        return new(subject, email, displayName, issuer);
    }

    private static string Required(IConfiguration config, string key, int maximum)
    {
        var value = config[key]?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw new DeploymentConfigurationException($"{key} is required and exceeds no more than {maximum} characters.");
        return value;
    }
}

public sealed record DeploymentOptions(string ConnectionString, string MigratorRole, string[] RuntimeRoles, bool Migrate, InitialAdministrator? Administrator)
{
    public static DeploymentOptions Read(IConfiguration config, string[] args)
    {
        if (args.Length == 0 || args.Any(x => x is not ("--migrate" or "--bootstrap-admin")) || args.Distinct().Count() != args.Length)
            throw new DeploymentConfigurationException("Only --migrate and the optional --bootstrap-admin are accepted by the deployment job.");
        var connectionString = DatabaseConnectionPolicy.ReadDeploymentConnection(config);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new DeploymentConfigurationException("Configure the dedicated migrator connection in ConnectionStrings:Database, Database:RailwayUrl or Deployment:RailwayUrl.");
        NpgsqlConnectionStringBuilder connection;
        try { connection = new(connectionString); }
        catch (ArgumentException) { throw new DeploymentConfigurationException("The migrator connection string is invalid."); }
        if (string.IsNullOrWhiteSpace(connection.Host) || string.IsNullOrWhiteSpace(connection.Database)
            || !ValidRoleName(connection.Username) || connection.Database is "postgres" or "template0" or "template1")
            throw new DeploymentConfigurationException("Use a named dedicated application database and a non-administrative migrator role.");
        connectionString = DatabaseConnectionPolicy.Validate(connectionString, config["ASPNETCORE_ENVIRONMENT"] == "Development");
        if (config.GetValue<bool>("Authentication:DemoEnabled") || config.GetValue<bool>("Database:Initialize") || config.GetValue<bool>("Seed:Enabled"))
            throw new DeploymentConfigurationException("Deployment jobs cannot enable demo authentication, automatic startup migrations or demo seeding.");
        var roles = config.GetSection("Database:RuntimeRoles").Get<string[]>() ?? [];
        if (roles.Length is < 1 or > 8 || roles.Any(role => !ValidRoleName(role)) || roles.Distinct(StringComparer.Ordinal).Count() != roles.Length
            || roles.Contains(connection.Username!, StringComparer.Ordinal))
            throw new DeploymentConfigurationException("Database:RuntimeRoles must list 1 to 8 distinct runtime roles different from the migrator.");
        return new(connectionString, connection.Username!, roles, args.Contains("--migrate"),
            args.Contains("--bootstrap-admin") ? InitialAdministrator.Read(config) : null);
    }

    public static bool ValidRoleName(string? role) => role is not null && role != "postgres" && !role.StartsWith("pg_", StringComparison.Ordinal)
        && Regex.IsMatch(role, "^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant);
}
