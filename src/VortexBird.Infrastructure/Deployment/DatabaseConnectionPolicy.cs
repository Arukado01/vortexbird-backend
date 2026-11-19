using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace VortexBird.Infrastructure.Deployment;

public static class DatabaseConnectionPolicy
{
    public const string RdsCaBundlePath = "/app/certs/aws-rds-global-bundle.pem";

    public static string? ReadConfiguredConnection(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Database");
        var railwayUrl = configuration["Database:RailwayUrl"];
        if (string.IsNullOrWhiteSpace(railwayUrl)) return connectionString;
        if (!string.IsNullOrWhiteSpace(connectionString))
            throw new DeploymentConfigurationException("Configure only one PostgreSQL connection source.");

        var connection = ReadRailwayConnection(railwayUrl, "Database:RailwayUrl");
        connection.RootCertificate = WriteRailwayTrustAnchor(configuration["Database:RootCertificatePem"]);
        return connection.ConnectionString;
    }

    internal static string? ReadDeploymentConnection(IConfiguration configuration)
    {
        const string setting = "Deployment:RailwayUrl";
        var migrationUrl = configuration[setting];
        if (migrationUrl is null) return ReadConfiguredConnection(configuration);

        var runtimeUrl = configuration["Database:RailwayUrl"];
        if (string.IsNullOrWhiteSpace(runtimeUrl) || !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Database")))
            throw new DeploymentConfigurationException($"{setting} requires Database:RailwayUrl as the only runtime connection source.");

        var runtime = ReadRailwayConnection(runtimeUrl, "Database:RailwayUrl");
        var migration = ReadRailwayConnection(migrationUrl, setting);
        if (!string.Equals(runtime.Host, migration.Host, StringComparison.OrdinalIgnoreCase) || runtime.Port != migration.Port ||
            runtime.Database != migration.Database || runtime.Username == migration.Username)
            throw new DeploymentConfigurationException($"{setting} must target the runtime database with a dedicated migration role.");

        migration.RootCertificate = WriteRailwayTrustAnchor(configuration["Database:RootCertificatePem"]);
        return migration.ConnectionString;
    }

    private static NpgsqlConnectionStringBuilder ReadRailwayConnection(string railwayUrl, string setting)
    {

        if (!Uri.TryCreate(railwayUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("postgres" or "postgresql") ||
            uri.HostNameType != UriHostNameType.Dns || !uri.Host.EndsWith(".railway.internal", StringComparison.Ordinal) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Port is 0 or > 65535)
        {
            throw new DeploymentConfigurationException($"{setting} must use a private Railway PostgreSQL hostname.");
        }

        var credentialSeparator = uri.UserInfo.IndexOf(':');
        var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        var role = credentialSeparator > 0 ? Uri.UnescapeDataString(uri.UserInfo[..credentialSeparator]) : "";
        var password = credentialSeparator > 0 ? Uri.UnescapeDataString(uri.UserInfo[(credentialSeparator + 1)..]) : "";
        if (!DeploymentOptions.ValidRoleName(database) || database is "template0" or "template1" ||
            !DeploymentOptions.ValidRoleName(role) || string.IsNullOrEmpty(password))
        {
            throw new DeploymentConfigurationException($"{setting} requires a named database and a dedicated PostgreSQL role with a password.");
        }

        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port < 0 ? 5432 : uri.Port,
            Database = database,
            Username = role,
            Password = password,
            SslMode = SslMode.VerifyFull,
            Timeout = 5,
            CommandTimeout = 30,
            MaxPoolSize = 10
        };
    }

    private static string WriteRailwayTrustAnchor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 65_536)
            throw new DeploymentConfigurationException("Database:RootCertificatePem requires the Railway database's public CA certificate.");

        string pem;
        try
        {
            var certificatePem = value.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
                ? value
                : Encoding.UTF8.GetString(Convert.FromBase64String(value));
            using var ca = X509Certificate2.CreateFromPem(certificatePem);
            if (!ca.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority))
                throw new DeploymentConfigurationException("Database:RootCertificatePem must contain the PostgreSQL CA certificate.");
            pem = ca.ExportCertificatePem();
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        {
            throw new DeploymentConfigurationException("Database:RootCertificatePem is not a valid public CA certificate.");
        }

        var certificatePath = Path.GetTempFileName();
        File.WriteAllText(certificatePath, pem);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { File.Delete(certificatePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
        return certificatePath;
    }

    public static string Validate(string connectionString, bool development)
    {
        NpgsqlConnectionStringBuilder connection;
        try { connection = new(connectionString); }
        catch (ArgumentException) { throw new DeploymentConfigurationException("The PostgreSQL connection string is invalid."); }
        if (string.IsNullOrWhiteSpace(connection.Host)) throw new DeploymentConfigurationException("A PostgreSQL host must be configured.");
        var localDevelopment = development && connection.Host.Split(',').All(host => host.Trim() is "localhost" or "127.0.0.1" or "::1" or "postgres");
        if (!localDevelopment && connection.SslMode != SslMode.VerifyFull)
            throw new DeploymentConfigurationException("PostgreSQL requires SSL Mode=VerifyFull outside local Development.");
        if (connection.Host.Split(',').Any(host => host.Trim().EndsWith(".rds.amazonaws.com", StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrWhiteSpace(connection.RootCertificate)) connection.RootCertificate = RdsCaBundlePath;
            if (!File.Exists(connection.RootCertificate))
                throw new DeploymentConfigurationException("The AWS RDS CA bundle is missing from the image; rebuild with the official CA bundle before deployment.");
        }
        return connection.ConnectionString;
    }
}
