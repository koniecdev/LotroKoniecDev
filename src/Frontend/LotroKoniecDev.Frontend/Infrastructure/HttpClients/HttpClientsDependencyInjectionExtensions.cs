using System.Net.Http.Json;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

public static class HttpClientsDependencyInjectionExtensions
{
    internal const int CircuitBreakerMinimumThroughput = 10;

    /// <summary>Per attempt, for ordinary JSON calls.</summary>
    internal static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Per attempt, for a multipart upload. It covers sending exported.txt (about 80 MB) plus the API's
    /// import, which takes minutes (spec 0003).
    /// </summary>
    internal static readonly TimeSpan UploadRequestTimeout = TimeSpan.FromMinutes(5);

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Every factory client starts on <see cref="CreatePrimaryHandler"/>, and one given its own standard
        /// transport still follows no redirect and keeps no cookie. So a new client is safe even when nobody
        /// remembers the setting (#922). It also reaches the OTLP exporter's clients, which need neither.
        /// </summary>
        public IServiceCollection AddHttpClientDefaults()
        {
            services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(CreatePrimaryHandler));

            // A post-configure also reaches a transport set by a client's own ConfigurePrimaryHttpMessageHandler.
            services.PostConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(builder => SwitchOffRedirectsAndCookies(builder.PrimaryHandler)));

            return services;
        }

        public IServiceCollection AddHttpClients()
        {
            services.AddTransient<TranslationContentNegotiationAndAuthDelegatingHandler>();

            services.AddHttpClient<ITranslationSystemClient, TranslationSystemClient>((sp, client) =>
                {
                    TranslationSystemSettings settings = sp
                        .GetRequiredService<IOptions<TranslationSystemSettings>>().Value;
                    client.BaseAddress = new Uri(settings.BaseUrl);

                    // The resilience pipeline owns the time limit. The default 100-second HttpClient.Timeout
                    // would cut a large upload short.
                    client.Timeout = Timeout.InfiniteTimeSpan;
                })
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
                .AddSameOriginHandler<TranslationSystemSettings>(settings => settings.BaseUrl)
                .AddHttpMessageHandler<TranslationContentNegotiationAndAuthDelegatingHandler>()
                // Outside the resilience handler, so a retry never adds the caller headers twice (ADR-0054).
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
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
                .AddSameOriginHandler<AuthSystemSettings>(settings => settings.BaseUrl)
                .AddHttpMessageHandler<AuthContentNegotiationAndAuthDelegatingHandler>()
                // Outside the resilience handler, so a retry never adds the caller headers twice (ADR-0054).
                .AddFrontendCallerHandler<AuthSystemSettings>(settings => settings.CallerKey)
                .AddResilienceHandler("AuthSystemResilience", ConfigureResiliencePipeline);

            return services;
        }
    }

    extension(IHttpClientBuilder builder)
    {
        /// <summary>It must be the first handler (#830).</summary>
        internal IHttpClientBuilder AddSameOriginHandler<TSettings>(Func<TSettings, string> readBaseUrl)
            where TSettings : class =>
            builder.AddHttpMessageHandler(serviceProvider => new SameOriginDelegatingHandler(
                new Uri(readBaseUrl(serviceProvider.GetRequiredService<IOptions<TSettings>>().Value)),
                serviceProvider.GetRequiredService<ILogger<SameOriginDelegatingHandler>>()));

        /// <summary>One box has one key today, but each API's settings carry their own copy (ADR-0054).</summary>
        internal IHttpClientBuilder AddFrontendCallerHandler<TSettings>(Func<TSettings, string?> readCallerKey)
            where TSettings : class =>
            builder.AddHttpMessageHandler(serviceProvider => new FrontendCallerDelegatingHandler(
                serviceProvider.GetRequiredService<IHttpContextAccessor>(),
                readCallerKey(serviceProvider.GetRequiredService<IOptions<TSettings>>().Value)));
    }

    /// <summary>
    /// No redirects: .NET drops <c>Authorization</c> on a redirect but sends every other header again, the
    /// caller key too, and a <c>307</c>/<c>308</c> sends the form body again (#830, #899). No cookies: one
    /// handler serves every visitor, so a kept cookie would go out with other visitors' calls (#924). The
    /// typed clients never replace their handler, so connections are renewed every 15 minutes for DNS.
    /// </summary>
    internal static SocketsHttpHandler CreatePrimaryHandler() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        AllowAutoRedirect = false,
        UseCookies = false
    };

    /// <summary>
    /// Acts on the standard transport at the end of the chain, for the reasons on
    /// <see cref="CreatePrimaryHandler"/>. Any other transport, such as a test's stand-in, is left alone.
    /// </summary>
    internal static void SwitchOffRedirectsAndCookies(HttpMessageHandler handler)
    {
        HttpMessageHandler? transport = handler;
        while (transport is DelegatingHandler delegatingHandler)
        {
            transport = delegatingHandler.InnerHandler;
        }

        switch (transport)
        {
            case SocketsHttpHandler socketsHandler:
                socketsHandler.AllowAutoRedirect = false;
                socketsHandler.UseCookies = false;
                break;
            case HttpClientHandler clientHandler:
                clientHandler.AllowAutoRedirect = false;
                clientHandler.UseCookies = false;
                break;
        }
    }

    internal static TimeSpan ResolveTimeout(HttpRequestMessage? request) =>
        request?.Content is MultipartFormDataContent
            ? UploadRequestTimeout
            : DefaultRequestTimeout;

    /// <summary>
    /// A multipart upload's body can be read only once, and an upload refused as too large will not pass
    /// on a second try. A password confirmation spends one permit of the account's budget per attempt
    /// (ADR-0053), and a timed-out attempt may still be served, so one click would spend several. The
    /// circuit breaker uses the same answer, so these failures never count toward opening it.
    /// </summary>
    internal static bool MayRetry(HttpRequestMessage? request) =>
        request?.Content switch
        {
            MultipartFormDataContent => false,
            JsonContent { ObjectType: { } bodyType } when bodyType.IsAssignableTo(typeof(IPasswordConfirmationRequest)) => false,
            _ => true
        };

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
            MinimumThroughput = CircuitBreakerMinimumThroughput,
            BreakDuration = TimeSpan.FromSeconds(30),
            ShouldHandle = args => ValueTask.FromResult(IsHandledTransientFailure(args.Outcome, args.Context))
        });

        pipeline.AddTimeout(new TimeoutStrategyOptions
        {
            TimeoutGenerator = args => ValueTask.FromResult(ResolveTimeout(args.Context.GetRequestMessage()))
        });
    }

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
