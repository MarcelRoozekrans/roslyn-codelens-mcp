namespace TestLib2;

using Microsoft.Extensions.DependencyInjection;
using TestLib;

public static class DiSetup
{
    public static IServiceCollection AddGreeting(this IServiceCollection services)
    {
        services.AddScoped<IGreeter, Greeter>();
        return services;
    }
}
