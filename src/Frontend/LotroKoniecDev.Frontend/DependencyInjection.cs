using System.Reflection;
using FluentValidation;
using LotroKoniecDev.Frontend.Infrastructure.Auth;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;

namespace LotroKoniecDev.Frontend;

public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddFrontend()
        {
            services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly(), includeInternalTypes: true);

            services.AddAntiforgery(options => options.Cookie.Path = "/");

            services.AddHttpClients();
            services.AddDiscoveryCache();
            services.AddFrontendAuthentication();

            return services;
        }
    }
}
