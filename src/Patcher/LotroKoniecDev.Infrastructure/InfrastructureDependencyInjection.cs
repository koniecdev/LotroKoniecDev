using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Infrastructure.DatFile;
using LotroKoniecDev.Infrastructure.Diagnostics;
using LotroKoniecDev.Infrastructure.GameLaunching;
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
    /// Service key for the <see cref="HttpClient"/> the TMS adapters use — the one that does not
    /// follow redirects, so the resolved endpoint's origin cannot be escaped after validation (#611).
    /// </summary>
    public const string TranslationSystemHttpClientKey = "translation-system";

    /// <summary>
    /// Adds infrastructure layer services to the service collection.
    /// </summary>
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        services.AddScoped<DatFileHandler>();
        services.AddScoped<IDatFileHandler>(sp => sp.GetRequiredService<DatFileHandler>());
        services.AddScoped<IDatVersionReader>(sp => sp.GetRequiredService<DatFileHandler>());
        services.AddSingleton<ILauncherSignatureVerifier, AuthenticodeLauncherSignatureVerifier>();
        services.AddSingleton<IGameLauncher, GameLauncher>();
        services.AddSingleton<IDatFileLocator, DatFileLocator>();
        services.AddSingleton<IGameProcessDetector, GameProcessDetector>();
        services.AddSingleton<IWriteAccessChecker, WriteAccessChecker>();
        services.AddSingleton<HttpClient>(_ => CreateHttpClient(followRedirects: true));

        // The TMS legs get their own client with redirects OFF (#611). The endpoint is resolved from
        // the service document and validated to be on the configured origin — a validation a 302 would
        // walk straight around, since the body would then come from the redirect target and its ETag
        // would hash that body, so the AUDIT-SEC-01 integrity check would happily confirm it. The
        // forum fetcher keeps redirects: it targets a third-party site that relies on them.
        services.AddKeyedSingleton<HttpClient>(
            TranslationSystemHttpClientKey, (_, _) => CreateHttpClient(followRedirects: false));

        services.AddSingleton<IForumPageFetcher, ForumPageFetcher>();
        services.AddSingleton<ITranslationSystemDiscoveryClient>(serviceProvider =>
            new TranslationSystemDiscoveryClient(
                serviceProvider.GetRequiredKeyedService<HttpClient>(TranslationSystemHttpClientKey)));
        services.AddSingleton<ITranslationFileDownloader>(serviceProvider =>
            new TranslationFileDownloader(
                serviceProvider.GetRequiredKeyedService<HttpClient>(TranslationSystemHttpClientKey)));
        services.AddSingleton<ITranslationFileCache, TranslationFileCache>();
        services.AddSingleton<IGameVersionFileStore, GameVersionFileStore>();
        services.AddSingleton<IFileHasher, FileHasher>();

        return services;
    }

    private static HttpClient CreateHttpClient(bool followRedirects)
    {
        HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = followRedirects });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LotroKoniecDev/1.0");
        client.Timeout = TimeSpan.FromSeconds(10);
        // Defense in depth (AUDIT-SEC-04 / #394): any future caller buffering a response
        // through the default completion option is still capped; today's callers enforce
        // their own tighter caps via BoundedResponseReader.
        client.MaxResponseContentBufferSize = TranslationFileDownloader.MaxResponseContentBytes;
        return client;
    }
}
