using System.Reflection;
using FluentValidation;
using LotroKoniecDev.Frontend.Components.Pages.Dashboard;
using LotroKoniecDev.Frontend.Components.Pages.Editor;
using LotroKoniecDev.Frontend.Components.Pages.Translations;
using LotroKoniecDev.Frontend.Infrastructure.Auth;
using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
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
            services.AddDeadSessionRegistry();
            services.AddDiscoveryCache();
            services.AddFrontendAuthentication();

            services.AddScoped<TranslationListLoader>();
            services.AddScoped<TranslationEditorLoader>();
            services.AddScoped<DashboardStatsLoader>();

            return services;
        }
    }
}
