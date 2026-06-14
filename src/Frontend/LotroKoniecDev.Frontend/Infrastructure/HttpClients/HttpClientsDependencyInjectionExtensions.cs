using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

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

        pipeline.AddTimeout(TimeSpan.FromSeconds(10));
    }

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
