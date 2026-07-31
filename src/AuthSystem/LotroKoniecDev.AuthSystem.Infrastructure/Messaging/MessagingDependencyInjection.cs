using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

internal static class MessagingDependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// The publisher is a singleton because it owns a TCP connection and a channel; a scoped
        /// registration would open and tear one down per request, which is the classic way to
        /// exhaust a broker's connection limit.
        /// </summary>
        public IServiceCollection AddMessaging()
        {
            services.AddSingleton<IMessagePublisher, RabbitMqMessagePublisher>();
            return services;
        }
    }
}
