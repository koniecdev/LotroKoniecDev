using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

internal static class MessagingDependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// The publisher is a singleton because it owns a TCP connection and a channel. Registering it
        /// as scoped would open and close one per request, which is the usual way to run a broker out
        /// of connections.
        /// </summary>
        public IServiceCollection AddMessaging()
        {
            services.AddSingleton<IMessagePublisher, RabbitMqMessagePublisher>();
            return services;
        }
    }
}
