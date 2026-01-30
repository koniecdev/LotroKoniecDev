using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Infrastructure.DatFile;
using LotroKoniecDev.Infrastructure.Diagnostics;
using LotroKoniecDev.Infrastructure.Network;
using LotroKoniecDev.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Infrastructure;

/// <summary>
/// Extension methods for registering infrastructure layer services.
/// </summary>
public static class InfrastructureDependencyInjection
{
    /// <summary>
    /// Adds infrastructure layer services to the service collection.
    /// </summary>
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        services.AddScoped<IDatFileHandler, DatFileHandler>();
        services.AddSingleton<IDatFileLocator, DatFileLocator>();
        services.AddSingleton<IGameProcessDetector, GameProcessDetector>();
        services.AddSingleton<IWriteAccessChecker, WriteAccessChecker>();
        services.AddSingleton<HttpClient>(_ =>
        {
            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LotroKoniecDev/1.0");
            client.Timeout = TimeSpan.FromSeconds(10);
            return client;
        });
        services.AddSingleton<IForumPageFetcher, ForumPageFetcher>();
        services.AddSingleton<IVersionFileStore, VersionFileStore>();

        return services;
    }
}
