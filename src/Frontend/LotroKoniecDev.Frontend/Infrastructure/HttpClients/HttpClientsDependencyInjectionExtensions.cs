using System.Net.Http.Json;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
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

                    // The resilience pipeline owns the time limit. HttpClient.Timeout is one limit for the
                    // whole pipeline and would default to 100 seconds, which would cut a large upload
                    // short. The per-attempt timeout below is chosen per kind of request instead.
                    client.Timeout = Timeout.InfiniteTimeSpan;
                })
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(15)
                })
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
                .AddHttpMessageHandler<TranslationContentNegotiationAndAuthDelegatingHandler>()
                // Outside the resilience handler on purpose: a retried request is the same message, so
                // the caller headers are added once and the TMS API sees one value each (ADR-0054, #823).
                .AddFrontendCallerHandler<TranslationSystemSettings>(settings => settings.CallerKey)
                .AddResilienceHandler("TranslationSystemResilience", ConfigureResiliencePipeline);

            services.AddTransient<AuthContentNegotiationAndAuthDelegatingHandler>();
            services.AddHttpContextAccessor();

            services.AddHttpClient<IAuthSystemClient, AuthSystemClient>((sp, client) =>
                {
                    AuthSystemSettings settings = sp
                        .GetRequiredService<IOptions<AuthSystemSettings>>().Value;
                    client.BaseAddress = new Uri(settings.BaseUrl);
                    client.Timeout = Timeout.InfiniteTimeSpan;
                })
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(15)
                })
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
                .AddHttpMessageHandler<AuthContentNegotiationAndAuthDelegatingHandler>()
                // Outside the resilience handler on purpose: a retried request is the same message, so
                // the caller headers are added once and the auth API sees one value each (ADR-0054).
                .AddFrontendCallerHandler<AuthSystemSettings>(settings => settings.CallerKey)
                .AddResilienceHandler("AuthSystemResilience", ConfigureResiliencePipeline);

            return services;
        }
    }

    extension(IHttpClientBuilder builder)
    {
        /// <summary>
        /// Adds <see cref="FrontendCallerDelegatingHandler"/> with the caller key of the API this client
        /// calls (ADR-0054). One box has one key today, but each API's settings carry their own copy.
        /// </summary>
        internal IHttpClientBuilder AddFrontendCallerHandler<TSettings>(Func<TSettings, string?> readCallerKey)
            where TSettings : class =>
            builder.AddHttpMessageHandler(serviceProvider => new FrontendCallerDelegatingHandler(
                serviceProvider.GetRequiredService<IHttpContextAccessor>(),
                readCallerKey(serviceProvider.GetRequiredService<IOptions<TSettings>>().Value)));
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

        // A normal JSON call keeps the short default, but a multipart upload carries exported.txt, about
        // 80 MB and growing, and then waits for the API's import, which takes minutes and not seconds.
        // The body can only be read once, so it is already excluded from retries below. The single
        // attempt simply needs more time than the default, or a healthy upload is cut off halfway
        // (spec 0003).
        pipeline.AddTimeout(new TimeoutStrategyOptions
        {
            TimeoutGenerator = args => ValueTask.FromResult(ResolveTimeout(args.Context.GetRequestMessage()))
        });
    }

    /// <summary>The time limit per attempt for ordinary JSON calls. It is short, so a stalled API fails fast.</summary>
    internal static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The time limit per attempt for a multipart upload. It has to cover sending exported.txt, about
    /// 80 MB, plus the API's import, which parses the file, computes the whole diff, saves it and rebuilds
    /// the artifact. So it is much longer than <see cref="DefaultRequestTimeout"/> (spec 0003, #208).
    /// </summary>
    internal static readonly TimeSpan UploadRequestTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Picks the timeout for a request: the long upload one when the body is a multipart form, which is
    /// the exported.txt upload, and otherwise the short default for ordinary JSON calls.
    /// </summary>
    internal static TimeSpan ResolveTimeout(HttpRequestMessage? request) =>
        request?.Content is MultipartFormDataContent
            ? UploadRequestTimeout
            : DefaultRequestTimeout;

    /// <summary>
    /// Whether the pipeline may send a request a second time. Two kinds of request may not.
    /// A multipart upload's body can only be read once, so a retry would send an already-consumed stream,
    /// and an upload refused for being too large, which shows up as a broken pipe rather than a 413, will
    /// not succeed on a second try either. Retrying it only multiplies the load and keeps the request
    /// waiting through every attempt and timeout.
    /// A request that carries a password confirmation spends one permit of the account's budget per
    /// attempt (ADR-0053). The retry fires on the per-attempt timeout as well, and a slow auth API still
    /// serves the first attempt, so one click would spend two or three permits.
    /// The same answer feeds the circuit breaker, so a failure on one of these requests does not count
    /// toward opening it, exactly as a multipart failure never did.
    /// </summary>
    internal static bool MayRetry(HttpRequestMessage? request) =>
        request?.Content switch
        {
            MultipartFormDataContent => false,
            JsonContent { ObjectType: { } bodyType } when bodyType.IsAssignableTo(typeof(IPasswordConfirmationRequest)) => false,
            _ => true
        };

    private static bool IsHandledTransientFailure(Outcome<HttpResponseMessage> outcome, ResilienceContext context)
    {
        if (!MayRetry(context.GetRequestMessage()))
        {
            return false;
        }

        return outcome.Result?.StatusCode >= System.Net.HttpStatusCode.InternalServerError
               || outcome.Exception is not null;
    }
}
