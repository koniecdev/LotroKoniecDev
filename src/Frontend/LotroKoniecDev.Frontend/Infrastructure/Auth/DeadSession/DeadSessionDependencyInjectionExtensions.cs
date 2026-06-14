namespace LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;

internal static class DeadSessionDependencyInjectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddDeadSessionRegistry()
        {
            services.AddScoped<IDeadSessionRegistry, DeadSessionRegistry>();
            services.AddScoped<ISessionExpiryNotice, SessionExpiryNotice>();
            return services;
        }
    }
}
