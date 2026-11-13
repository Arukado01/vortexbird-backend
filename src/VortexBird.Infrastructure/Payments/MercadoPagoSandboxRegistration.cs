using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using VortexBird.Application.Abstractions;

namespace VortexBird.Infrastructure.Payments;

public static class MercadoPagoSandboxRegistration
{
    /// <summary>Client-only registration retained for isolated adapter verification.</summary>
    public static IServiceCollection AddMercadoPagoSandboxPilot(this IServiceCollection services, MercadoPagoSandboxOptions options)
    {
        services.AddSingleton(options);
        services.AddHttpClient<MercadoPagoSandboxClient>(client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services;
    }

    /// <summary>Simulator stays the default. Sandbox requires two explicit switches and complete test-account configuration.</summary>
    public static IServiceCollection AddPaymentProcessing(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Payments:Provider"] ?? "Simulator";
        if (provider is not ("Simulator" or "MercadoPagoSandbox")) throw new InvalidOperationException("Payments:Provider debe ser Simulator o MercadoPagoSandbox.");
        var enabled = provider == "MercadoPagoSandbox";
        var section = configuration.GetSection("Payments:Sandbox");
        var secret = section["WebhookSecret"] ?? "";
        MercadoPagoSandboxOptions options;
        if (enabled)
        {
            if (!section.GetValue<bool>("Enabled") || string.IsNullOrWhiteSpace(section["AccessToken"]) || !long.TryParse(section["ExpectedTestSellerId"], out var seller) || seller <= 0 || secret.Length < 32
                || !Uri.TryCreate(section["NotificationUrl"], UriKind.Absolute, out var notification) || !Uri.TryCreate(section["ReturnUrl"], UriKind.Absolute, out var returnUrl))
                throw new InvalidOperationException("Sandbox requiere habilitación explícita, vendedor de prueba, secreto webhook y URLs HTTPS configurados.");
            options = new(true, section["AccessToken"]!, seller, notification, returnUrl);
            services.Replace(ServiceDescriptor.Scoped<IPaymentGateway, MercadoPagoSandboxGateway>());
            if (configuration.GetValue("Workers:Enabled", true)) services.AddHostedService<PaymentWorker>();
        }
        else options = new(false, "", 0, new Uri("https://disabled.invalid/notification"), new Uri("https://disabled.invalid/return"));
        services.AddSingleton(new PaymentRuntimeOptions(enabled, secret));
        services.AddMercadoPagoSandboxPilot(options);
        services.AddScoped<PaymentCheckoutService>();
        services.AddScoped<PaymentReconciler>();
        return services;
    }
}
