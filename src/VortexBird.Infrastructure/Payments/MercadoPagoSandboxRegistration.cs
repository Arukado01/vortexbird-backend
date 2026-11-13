using Microsoft.Extensions.DependencyInjection;

namespace VortexBird.Infrastructure.Payments;

public static class MercadoPagoSandboxRegistration
{
    /// <summary>Explicit pilot registration; does not replace IPaymentGateway or expose an endpoint.</summary>
    public static IServiceCollection AddMercadoPagoSandboxPilot(this IServiceCollection services, MercadoPagoSandboxOptions options)
    {
        services.AddSingleton(options);
        services.AddHttpClient<MercadoPagoSandboxClient>(client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services;
    }
}
