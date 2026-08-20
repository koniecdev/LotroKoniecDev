using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Infrastructure.DatFile;
using LotroKoniecDev.Infrastructure.Diagnostics;
using LotroKoniecDev.Infrastructure.GameLaunching;
using LotroKoniecDev.Infrastructure.Network;
using LotroKoniecDev.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;


namespace LotroKoniecDev.Infrastructure;

public static class InfrastructureDependencyInjection
{
    /// <summary>
    /// The service key of the <see cref="HttpClient"/> the TMS adapters use. That client does not
    /// follow redirects, so a request cannot leave the origin we validated (#611).
    /// </summary>
    public const string TranslationSystemHttpClientKey = "translation-system";

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

        // The TMS calls get their own client with redirects turned off (#611). We take the endpoint
        // from the service document and check that it is on the configured origin, and a 302 would
        // simply walk around that check: the body would come from the redirect target, and its ETag
        // would hash that body, so the AUDIT-SEC-01 integrity check would confirm the wrong file.
        // The forum fetcher keeps redirects, because it calls a third-party site that needs them.
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
        services.AddSingleton<ITranslationLedger, TranslationLedger>();
        services.AddSingleton<IGameVersionFileStore, GameVersionFileStore>();
        services.AddSingleton<IFileHasher, FileHasher>();

        return services;
    }

    private static HttpClient CreateHttpClient(bool followRedirects)
    {
        HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = followRedirects });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LotroKoniecDev/1.0");
        client.Timeout = TimeSpan.FromSeconds(10);
        // A second line of defence (AUDIT-SEC-04, #394). If a future caller buffers a response the
        // default way, it is still capped here. Today's callers set their own lower caps through
        // BoundedResponseReader.
        client.MaxResponseContentBufferSize = TranslationFileDownloader.MaxResponseContentBytes;
        return client;
    }
}
