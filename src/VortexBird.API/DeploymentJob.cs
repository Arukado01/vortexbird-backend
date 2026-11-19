using System.Text.Json;
using VortexBird.Infrastructure.Deployment;

namespace VortexBird.API;

public static class DeploymentJob
{
    private const string Bind = "--bind-pilot-roles";

    public static async Task<int> Run(string[] args)
    {
        try
        {
            // Deliberately bypass WebApplication/DI: no listener, worker, demo login or demo seed starts in this job.
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            if (args.Contains(Bind, StringComparer.Ordinal) && args.Count(argument => argument == Bind) == 1
                && !args.Contains("--migrate", StringComparer.Ordinal) && !args.Contains("--bootstrap-admin", StringComparer.Ordinal))
            {
                // Runtime binding-only mode: the runtime role already owns exactly the grants the
                // binding needs (roles, permissions, users, user roles, activity log); no migrator
                // credentials or migrations are involved.
                var pilot = ReadPilotManifest(configuration);
                using var bindingTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var connection = DatabaseConnectionPolicy.ReadConfiguredConnection(configuration)
                    ?? throw new DeploymentConfigurationException("Configure the runtime PostgreSQL connection for the pilot role binding.");
                try
                {
                    await pilot.Run(connection, bindingTimeout.Token);
                }
                catch (InvalidOperationException error)
                {
                    // Binding validation messages reference manifest identities only; they contain no secrets.
                    Console.Error.WriteLine(JsonSerializer.Serialize(new { operation = "cognito_role_binding", status = "rejected", detail = error.Message }));
                    return 1;
                }
                Console.WriteLine(JsonSerializer.Serialize(new { operation = "cognito_role_binding", status = "completed",
                    cognitoBindings = pilot.Accounts.Length, testAccounts = pilot.TestAccounts.Length }));
                return 0;
            }

            var pilotBootstrap = ReadPilotBootstrap(configuration, ref args);
            var options = DeploymentOptions.Read(configuration, args);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await DatabaseDeployment.Run(options, timeout.Token);
            if (pilotBootstrap is not null) await pilotBootstrap.Run(options.ConnectionString, timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { operation = "database_deployment", status = "completed", migrations = options.Migrate,
                administratorBootstrap = options.Administrator is not null, cognitoBindings = pilotBootstrap?.Accounts.Length ?? 0,
                testAccounts = pilotBootstrap?.TestAccounts.Length ?? 0 }));
            return 0;
        }
        catch (DeploymentConfigurationException error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { operation = "database_deployment", status = "invalid_configuration", detail = error.Message }));
            return 2;
        }
        catch (DeploymentPolicyException error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { operation = "database_deployment", status = "failed", errorCode = "deployment_policy", detail = error.Message }));
            return 1;
        }
        catch (Exception error)
        {
            // Provider exceptions can contain connection or row values. Keep configuration and identities out of job logs.
            Console.Error.WriteLine(JsonSerializer.Serialize(new { operation = "database_deployment", status = "failed", errorType = error.GetType().Name,
                detail = "Migration, role validation or initial identity bootstrap failed. Review database policy and deployment configuration; no application service was started." }));
            return 1;
        }
    }

    private static PilotRoleBootstrap ReadPilotManifest(IConfiguration configuration)
    {
        var issuer = (configuration["Authentication:ValidIssuer"] ?? configuration["Authentication:Authority"])?.TrimEnd('/') ?? "";
        try
        {
            using var manifest = JsonDocument.Parse(configuration["Bootstrap:CognitoPilotManifest"] ?? "");
            return PilotRoleBootstrap.Read(manifest.RootElement, issuer);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new DeploymentConfigurationException("Bootstrap:CognitoPilotManifest must match the configured Cognito issuer, the nine pilot accounts and the optional test accounts.");
        }
    }

    private static PilotRoleBootstrap? ReadPilotBootstrap(IConfigurationRoot configuration, ref string[] args)
    {
        if (!args.Contains(Bind, StringComparer.Ordinal)) return null;
        if (args.Count(argument => argument == Bind) != 1 || args.Contains("--bootstrap-admin", StringComparer.Ordinal))
            throw new DeploymentConfigurationException("Use --bind-pilot-roles once instead of --bootstrap-admin for the Cognito pilot.");

        var pilot = ReadPilotManifest(configuration);
        var administrator = pilot.Administrator;
        configuration["Bootstrap:AdminSubject"] = administrator.Subject;
        configuration["Bootstrap:AdminEmail"] = administrator.Email;
        configuration["Bootstrap:AdminDisplayName"] = "Piloto SUPER_ADMIN";
        configuration["Bootstrap:OidcIssuer"] = (configuration["Authentication:ValidIssuer"] ?? configuration["Authentication:Authority"])?.TrimEnd('/') ?? "";
        args = args.Select(argument => argument == Bind ? "--bootstrap-admin" : argument).ToArray();
        return pilot;
    }
}
