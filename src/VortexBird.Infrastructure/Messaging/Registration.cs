using Amazon;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace VortexBird.Infrastructure.Messaging;

public static class MessagingRegistration
{
    public static IServiceCollection AddIntegrationMessaging(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var options = configuration.GetSection("Messaging").Get<MessagingOptions>() ?? new MessagingOptions();
        if (options.Provider is not ("local" or "sqs") || options.MaxAttempts is < 1 or > 20 || options.VisibilityTimeoutSeconds is < 30 or > 900) throw new InvalidOperationException("Invalid messaging configuration.");
        services.AddSingleton(options);
        services.AddScoped<IInboxStore, PostgresInboxStore>();
        services.AddScoped<IIntegrationEventHandler, IntegrationEventHandler>();
        services.AddScoped<DurableEventConsumer>();
        services.AddScoped<OutboxDispatcher>();
        if (options.IsSqs)
        {
            var config = BuildClientConfig(options, environment.IsDevelopment());
            if (options.ServiceUrl is null && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_WEB_IDENTITY_TOKEN_FILE")))
            {
                try { _ = System.Reflection.Assembly.Load("AWSSDK.SecurityToken"); }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException)
                {
                    throw new InvalidOperationException("IRSA requires the official AWSSDK.SecurityToken runtime dependency. Restore it or configure EKS Pod Identity before enabling SQS.", ex);
                }
            }
            services.AddSingleton(_ => options.ServiceUrl is null
                ? new SqsJsonClient(config)
                : new SqsJsonClient(new BasicAWSCredentials("vortexbird-local-only", "vortexbird-local-only"), config));
            services.AddSingleton<IQueueTransport, SqsQueueTransport>();
            if (configuration.GetValue("Workers:Enabled", true)) services.AddHostedService<SqsConsumerWorker>();
        }
        if (configuration.GetValue("Workers:Enabled", true)) services.AddHostedService<OutboxWorker>();
        return services;
    }

    public static SqsClientConfig BuildClientConfig(MessagingOptions options, bool development)
    {
        if (!Uri.TryCreate(options.QueueUrl, UriKind.Absolute, out var queue) || !string.IsNullOrEmpty(queue.UserInfo) || !string.IsNullOrEmpty(queue.Query) || !string.IsNullOrEmpty(queue.Fragment)) throw new InvalidOperationException("Invalid SQS queue URL.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(options.Region, "^[a-z]{2}-[a-z]+-[1-9]$")) throw new InvalidOperationException("Invalid AWS region.");
        string service;
        if (options.ServiceUrl is not null)
        {
            if (!development || !Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out var local) || local.Scheme != "http" || local.Host is not ("localhost" or "127.0.0.1" or "elasticmq") || !string.IsNullOrEmpty(local.UserInfo) || local.AbsolutePath != "/" || queue.GetLeftPart(UriPartial.Authority) != local.GetLeftPart(UriPartial.Authority)) throw new InvalidOperationException("Local SQS requires a Development emulator endpoint and matching queue origin.");
            service = local.GetLeftPart(UriPartial.Authority);
        }
        else
        {
            if (queue.Scheme != "https" || queue.Port != 443 || queue.Host != $"sqs.{options.Region}.amazonaws.com" || !System.Text.RegularExpressions.Regex.IsMatch(queue.AbsolutePath, "^/[0-9]{12}/[A-Za-z0-9_-]+(?:\\.fifo)?$")) throw new InvalidOperationException("SQS queue must use the selected regional AWS endpoint and account/queue path.");
            service = queue.GetLeftPart(UriPartial.Authority);
        }
        return new SqsClientConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region), AuthenticationRegion = options.Region, ServiceURL = service, UseHttp = options.ServiceUrl is not null };
    }
}
