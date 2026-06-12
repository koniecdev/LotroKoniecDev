using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.Infrastructure.Emails;
using LotroKoniecDev.AuthSystem.Infrastructure.Options;

namespace LotroKoniecDev.AuthSystem.Infrastructure;

public static class InfrastructureDependencyInjection
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddInfrastructure()
        {
            services.AddSingleton(TimeProvider.System);
            services.AddValidatorsFromAssembly(typeof(IInfrastructureMarker).Assembly);
            services.AddInfrastructureOptions();
            services.AddEmails();

            return services;
        }
    }
}
