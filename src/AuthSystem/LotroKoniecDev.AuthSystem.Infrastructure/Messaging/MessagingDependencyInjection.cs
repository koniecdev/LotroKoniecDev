using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

internal static class MessagingDependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// The publisher is a singleton because it owns a TCP connection and a channel. A scoped one would
        /// open a connection per request and run the broker out of connections.
        /// </summary>
        public IServiceCollection AddMessaging()
        {
            services.AddSingleton<IMessagePublisher, RabbitMqMessagePublisher>();
            return services;
        }
    }
}
