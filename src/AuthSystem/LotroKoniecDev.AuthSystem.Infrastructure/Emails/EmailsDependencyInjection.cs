using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.AuthSystem.Infrastructure.Emails;

internal static class EmailsDependencyInjection
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddEmails()
        {
            services.AddScoped<IEmailService, EmailService>();
            return services;
        }
    }
}
