using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

public static class HttpClientsDependencyInjectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddHttpClients()
        {
            services.AddTransient<TranslationContentNegotiationAndAuthDelegatingHandler>();

            services.AddHttpClient<ITranslationSystemClient, TranslationSystemClient>((sp, client) =>
                {
                    TranslationSystemSettings settings = sp
                        .GetRequiredService<IOptions<TranslationSystemSettings>>().Value;
                    client.BaseAddress = new Uri(settings.BaseUrl);

                    // Let the resilience pipeline own the time budget: HttpClient.Timeout is a single cap
                    // across the whole pipeline (and would otherwise default to 100 s, cutting a large
                    // upload short), whereas the per-attempt timeout below scales with the request kind.
                    client.Timeout = Timeout.InfiniteTimeSpan;
                })
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(15)
                })
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
                .AddHttpMessageHandler<TranslationContentNegotiationAndAuthDelegatingHandler>()
                .AddResilienceHandler("TranslationSystemResilience", ConfigureResiliencePipeline);

            return services;
        }
    }

    private static void ConfigureResiliencePipeline(ResiliencePipelineBuilder<HttpResponseMessage> pipeline)
    {
        pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 2,
            Delay = TimeSpan.FromMilliseconds(500),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            ShouldHandle = args => ValueTask.FromResult(IsHandledTransientFailure(args.Outcome, args.Context))
        });

        pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            SamplingDuration = TimeSpan.FromSeconds(30),
            FailureRatio = 0.5,
            MinimumThroughput = 10,
            BreakDuration = TimeSpan.FromSeconds(30),
            ShouldHandle = args => ValueTask.FromResult(IsHandledTransientFailure(args.Outcome, args.Context))
        });

        // A normal JSON call keeps the tight default budget, but a multipart upload carries exported.txt
        // (~80 MB and growing) and then waits on the API's synchronous import — minutes, not seconds. The
        // body is forward-only so it is already excluded from retries (below); the single attempt simply
        // needs a wider window than the default, or a healthy upload is aborted mid-flight (spec 0003).
        pipeline.AddTimeout(new TimeoutStrategyOptions
        {
            TimeoutGenerator = args => ValueTask.FromResult(ResolveTimeout(args.Context.GetRequestMessage()))
        });
    }

    /// <summary>The per-attempt budget for ordinary (JSON) calls — kept tight so a stalled API fails fast.</summary>
    internal static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The per-attempt budget for a multipart upload: it must cover transferring exported.txt (~80 MB)
    /// plus the API's synchronous import (parse + full diff + save + artifact rebuild), so it is far
    /// longer than <see cref="DefaultRequestTimeout"/> (spec 0003, #208).
    /// </summary>
    internal static readonly TimeSpan UploadRequestTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Picks the resilience timeout for a request: the wide upload budget when the body is a multipart
    /// form (the exported.txt upload), otherwise the tight default for ordinary JSON calls.
    /// </summary>
    internal static TimeSpan ResolveTimeout(HttpRequestMessage? request) =>
        request?.Content is MultipartFormDataContent
            ? UploadRequestTimeout
            : DefaultRequestTimeout;

    /// <summary>
    /// Multipart uploads are excluded: their body stream is forward-only and cannot be replayed, so a
    /// retry would re-send an exhausted stream, and a rejected oversized upload (413 surfacing as a
    /// broken-pipe write error) is not transient. Replaying it only multiplies the load and stalls the
    /// request through every attempt and timeout.
    /// </summary>
    private static bool IsHandledTransientFailure(Outcome<HttpResponseMessage> outcome, ResilienceContext context)
    {
        if (context.GetRequestMessage()?.Content is MultipartFormDataContent)
        {
            return false;
        }

        return outcome.Result?.StatusCode >= System.Net.HttpStatusCode.InternalServerError
               || outcome.Exception is not null;
    }
}
