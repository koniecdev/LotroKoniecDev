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

        // The TMS calls get their own client with redirects off (#611). The endpoint from the service
        // document is checked against the configured origin, and a 302 would walk around that check:
        // the redirect target would serve both the body and the ETag that hashes it, so the integrity
        // check (#391) would confirm the wrong file. The forum fetcher keeps redirects: it calls a
        // third-party site.
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
        services.AddSingleton<ISubFileIterationSnapshotStore, SubFileIterationSnapshotStore>();
        services.AddSingleton<IGameVersionFileStore, GameVersionFileStore>();
        services.AddSingleton<IFileHasher, FileHasher>();

        return services;
    }

    private static HttpClient CreateHttpClient(bool followRedirects)
    {
        HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = followRedirects });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LotroKoniecDev/1.0");
        client.Timeout = TimeSpan.FromSeconds(10);
        // A second line of defence (#394): a caller that buffers a response the default way is still
        // capped here. Today's callers set their own lower caps through BoundedResponseReader.
        client.MaxResponseContentBufferSize = TranslationFileDownloader.MaxResponseContentBytes;
        return client;
    }
}
