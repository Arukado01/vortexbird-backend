using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Configuration;
using Npgsql;
using VortexBird.Domain.Entities;
using VortexBird.Infrastructure;
using VortexBird.Infrastructure.Deployment;

internal static class DeploymentChecks
{
    public static void Run(IntegrationDbContext db, Action<bool, string> check, Action<Action, string> reject)
    {
        Dictionary<string, string?> Configuration() => new()
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["ConnectionStrings:Database"] = "Host=db.example.com;Database=vortexbird;Username=vb_migrator;SSL Mode=VerifyFull",
            ["Database:RuntimeRoles:0"] = "vb_api", ["Database:RuntimeRoles:1"] = "vb_worker",
            ["Bootstrap:AdminSubject"] = "oidc-123", ["Bootstrap:AdminEmail"] = "admin@example.com",
            ["Bootstrap:AdminDisplayName"] = "Initial administrator", ["Bootstrap:OidcIssuer"] = "https://id.example.com/realms/vb",
            ["Authentication:ValidIssuer"] = "https://id.example.com/realms/vb"
        };
        DeploymentOptions Read(Dictionary<string, string?> values, params string[] args) => DeploymentOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), args);
        void RejectSetting(string key, string? value, string message)
        {
            var config = Configuration(); config[key] = value;
            reject(() => Read(config, "--migrate", "--bootstrap-admin"), message);
        }
        var valid = Read(Configuration(), "--migrate", "--bootstrap-admin");
        check(valid.Migrate && valid.RuntimeRoles.Length == 2 && valid.Administrator?.Subject == "oidc-123", "Deployment selects distinct runtime identities and explicit OIDC administrator");
        check(Read(Configuration(), "--migrate").Administrator is null, "Normal release does not bootstrap an administrator");
        check(!Read(Configuration(), "--bootstrap-admin").Migrate, "Standalone bootstrap does not silently migrate");
        reject(() => Read(Configuration(), "--migrate", "--urls=http://0.0.0.0"), "Deployment rejects server arguments instead of starting a listener");
        reject(() => Read(Configuration(), "--migrate", "--migrate"), "Deployment rejects duplicate command flags");
        RejectSetting("ConnectionStrings:Database", null, "Migration refuses a missing dedicated connection");
        RejectSetting("Database:RuntimeRoles:0", "vb_migrator", "Migrator credentials cannot be used as runtime");
        RejectSetting("Database:RuntimeRoles:1", "vb_api", "Runtime identity entries must be distinct");
        RejectSetting("Database:RuntimeRoles:0", "pg_read_all_data", "Built-in broad PostgreSQL role cannot be a runtime role");
        RejectSetting("Database:RuntimeRoles:0", "vb_api;DROP ROLE postgres", "SQL role-name injection rejected before connection");
        RejectSetting("ConnectionStrings:Database", "Host=db.example.com;Database=postgres;Username=vb_migrator;SSL Mode=VerifyFull", "Migration refuses the maintenance database");
        RejectSetting("ConnectionStrings:Database", "Host=db.example.com;Database=vortexbird;Username=vb_migrator;SSL Mode=Require", "Production requires hostname and trust validation, not encryption only");
        foreach (var flag in new[] { "Authentication:DemoEnabled", "Database:Initialize", "Seed:Enabled" })
            RejectSetting(flag, "true", "Deployment prohibits " + flag);
        RejectSetting("Bootstrap:AdminSubject", "demo-admin", "Production bootstrap refuses the demo identity");
        RejectSetting("Bootstrap:AdminEmail", "invalid-mail", "Bootstrap refuses invalid email before database access");
        RejectSetting("Bootstrap:OidcIssuer", "http://id.example.com/realms/vb", "Bootstrap requires HTTPS identity provider");
        RejectSetting("Bootstrap:OidcIssuer", "https://another.example.com/realms/vb", "Bootstrap issuer must match the API validator");
        var development = Configuration(); development["ASPNETCORE_ENVIRONMENT"] = "Development";
        development["ConnectionStrings:Database"] = "Host=localhost;Database=vortexbird;Username=vb_migrator;SSL Mode=Disable";
        check(Read(development, "--migrate").Migrate, "Explicit local Development permits a loopback database without TLS");
        development["ConnectionStrings:Database"] = "Host=postgres;Database=vortexbird;Username=vb_migrator;SSL Mode=Disable";
        check(Read(development, "--migrate").Migrate, "Development preserves the existing Docker Compose postgres service");
        development["ConnectionStrings:Database"] = "Host=db.example.com;Database=vortexbird;Username=vb_migrator;SSL Mode=Disable";
        reject(() => Read(development, "--migrate"), "Development cannot disable TLS for a remote database");
        reject(() => DatabaseConnectionPolicy.Validate("Host=instance.rds.amazonaws.com;Database=vb;Username=vb_api;SSL Mode=VerifyFull;Root Certificate=/missing-vortexbird-qa-ca.pem", false), "RDS connection fails closed when its configured CA bundle is absent");
        using var signingKey = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=Railway QA CA", signingKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, critical: true));
        using var ca = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var railway = Configuration();
        railway["ConnectionStrings:Database"] = null;
        railway["Database:RailwayUrl"] = "postgresql://vb_migrator:qa%40pw@vortexbird-postgres.railway.internal:5432/vortexbird_app";
        railway["Database:RootCertificatePem"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(ca.ExportCertificatePem()));
        var railwayConnection = new NpgsqlConnectionStringBuilder(Read(railway, "--migrate").ConnectionString);
        check(railwayConnection.Host == "vortexbird-postgres.railway.internal" && railwayConnection.Password == "qa@pw" &&
            railwayConnection.SslMode == SslMode.VerifyFull && File.Exists(railwayConnection.RootCertificate),
            "Railway migration keeps a private host and verifies the dedicated PostgreSQL CA and hostname");
        var workerPredeploy = new Dictionary<string, string?>(railway)
        {
            ["Database:RailwayUrl"] = "postgresql://vb_worker:qa@vortexbird-postgres.railway.internal:5432/vortexbird_app",
            ["Deployment:RailwayUrl"] = railway["Database:RailwayUrl"]
        };
        var runtimeConnection = new NpgsqlConnectionStringBuilder(DatabaseConnectionPolicy.ReadConfiguredConnection(
            new ConfigurationBuilder().AddInMemoryCollection(workerPredeploy).Build()));
        check(runtimeConnection.Username == "vb_worker" && Read(workerPredeploy, "--migrate").MigratorRole == "vb_migrator",
            "Worker runtime keeps its own role while the predeploy job selects dedicated migration credentials");
        workerPredeploy["Deployment:RailwayUrl"] = "postgresql://vb_migrator:qa@another-postgres.railway.internal:5432/vortexbird_app";
        reject(() => Read(workerPredeploy, "--migrate"), "Worker predeploy rejects a different database service");
        workerPredeploy["Deployment:RailwayUrl"] = workerPredeploy["Database:RailwayUrl"];
        reject(() => Read(workerPredeploy, "--migrate"), "Worker predeploy rejects runtime credentials for migrations");
        railway["Database:RootCertificatePem"] = null;
        reject(() => Read(railway, "--migrate"), "Railway refuses to connect without the configured database CA");
        railway["Database:RailwayUrl"] = "postgresql://vb_migrator:unused@public.proxy.rlwy.net:5432/vortexbird_app";
        reject(() => Read(railway, "--migrate"), "Railway refuses the public PostgreSQL proxy");
        RuntimeDatabasePermissions.ValidateModel(db);
        check(true, "Every EF application table has an explicit runtime policy, with no unreviewed sequences");
        check(RuntimeDatabasePermissions.Tables["ActivityLogs"] == "SELECT, INSERT", "Runtime audit is append-only at the database grant boundary");
        check(RuntimeDatabasePermissions.Tables["__EFMigrationsHistory"] == "SELECT", "Runtime cannot alter EF migration history");
        check(RuntimeDatabasePermissions.Tables.Where(x => x.Value.Contains("DELETE", StringComparison.Ordinal)).Select(x => x.Key).ToHashSet().SetEquals(["Roles", "UserRoles", "RolePermissions"]), "Only role administration tables grant runtime deletion");
        reject(() => RuntimeDatabasePermissions.GrantStatement("UnrelatedTable", "vb_api"), "Grant policy refuses unrelated tables");
        reject(() => RuntimeDatabasePermissions.GrantStatement("ActivityLogs", "vb_api\";DROP TABLE x"), "Grant policy rejects injected role identifiers");

        var requested = valid.Administrator!;
        var role = new Role { Name = "SUPER_ADMIN", Protected = true };
        User Existing(bool enabled = true, Guid? partner = null, string email = "admin@example.com") => new() { Subject = requested.Subject, Email = email, Enabled = enabled, PartnerId = partner };
        InitialAdminBootstrap.ValidateExisting(requested, null, null, [], false);
        check(true, "Bootstrap permits only the first identity in an empty installation");
        InitialAdminBootstrap.ValidateExisting(requested, Existing(), role, [requested.Subject], true);
        check(true, "Repeating the same enabled global administrator is idempotent");
        reject(() => InitialAdminBootstrap.ValidateExisting(requested, null, role, ["other-subject"], true), "Bootstrap cannot replace or add to another administrator");
        reject(() => InitialAdminBootstrap.ValidateExisting(requested, Existing(false), role, [requested.Subject], true), "Bootstrap cannot reactivate a disabled administrator");
        reject(() => InitialAdminBootstrap.ValidateExisting(requested, Existing(partner: Guid.NewGuid()), role, [requested.Subject], true), "Bootstrap cannot promote a tenant-scoped identity to global access");
        reject(() => InitialAdminBootstrap.ValidateExisting(requested, Existing(email: "other@example.com"), role, [requested.Subject], true), "Bootstrap cannot relink an administrator email");
        reject(() => InitialAdminBootstrap.ValidateExisting(requested, Existing(), role, [], true), "Bootstrap cannot silently promote an existing non-administrator");
        reject(() => InitialAdminBootstrap.ValidateExisting(requested, null, role, [], true), "Bootstrap cannot add a first admin to an already populated identity table");
        reject(() => InitialAdminBootstrap.ValidateExisting(requested, Existing(), new Role { Name = "SUPER_ADMIN", Protected = false }, [requested.Subject], true), "Bootstrap rejects a preexisting unprotected SUPER_ADMIN role");
    }
}
