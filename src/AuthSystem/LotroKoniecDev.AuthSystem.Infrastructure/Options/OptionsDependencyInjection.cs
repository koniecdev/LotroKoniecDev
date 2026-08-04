using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.Infrastructure.Emails;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using LotroKoniecDev.Options;

namespace LotroKoniecDev.AuthSystem.Infrastructure.Options;

internal static class OptionsDependencyInjection
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddInfrastructureOptions()
        {
            services.AddOptionsWithFluentValidation<EmailOptions>(EmailOptions.ConfigurationSection);
            services.AddOptionsWithFluentValidation<RabbitMqOptions>(RabbitMqOptions.ConfigurationSection);

            return services;
        }
    }
}
