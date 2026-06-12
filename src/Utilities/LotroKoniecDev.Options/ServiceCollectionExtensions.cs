using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Options;

public static class ServiceCollectionExtensions
{
    extension<TOptions>(IServiceCollection services) where TOptions : class
    {
        public IServiceCollection AddOptionsWithFluentValidation(string configurationSection)
        {
            services
                .AddOptions<TOptions>()
                .BindConfiguration(configurationSection)
                .ValidateFluentValidation()
                .ValidateOnStart();

            return services;
        }
    }
}
