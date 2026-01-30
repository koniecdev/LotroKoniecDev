using LotroKoniecDev.CLI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.CLI;

public static class CliDependencyInjection
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddCliServices()
        {
            services.AddSingleton<App>();
            
            return services;
        }
    }
}
