using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;

internal static class PwnedPasswordsDependencyInjection
{
    /// <summary>
    /// Needs no API key and has no rate limit (haveibeenpwned.com/API/v3, checked 2026-10-07).
    /// </summary>
    internal static readonly Uri BaseAddress = new("https://api.pwnedpasswords.com/");

    /// <summary>
    /// Short, because a person waits on the form and the endpoint normally answers in well under a
    /// second. After this the password goes through unchecked (ADR-0066).
    /// </summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A padded answer is about 80 KB today. The cap leaves room for growth and keeps a broken answer from
    /// filling memory.
    /// </summary>
    internal const long MaxResponseContentBytes = 1024 * 1024;

    extension(IServiceCollection services)
    {
        public IServiceCollection AddPwnedPasswords()
        {
            services.AddMemoryCache();

            // No resilience handler: a retry would only make the person wait longer for an optional answer.
            services.AddHttpClient<IPwnedPasswordChecker, PwnedPasswordChecker>(client =>
                {
                    client.BaseAddress = BaseAddress;
                    client.Timeout = RequestTimeout;
                    client.MaxResponseContentBufferSize = MaxResponseContentBytes;
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("LotroKoniecDev-AuthSystem/1.0");
                })
                .ConfigurePrimaryHttpMessageHandler(CreatePrimaryHandler)
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
                // The factory's handlers log every request URL, and this one ends in the hash prefix
                // (ADR-0066).
                .RemoveAllLoggers();

            return services;
        }
    }

    /// <summary>
    /// No redirects, so a request cannot leave the chosen host, and no cookies, because one handler serves
    /// every request. The pooled connection lifetime keeps DNS fresh, since the handler is never rotated.
    /// No activity propagator, so no <c>traceparent</c> or <c>baggage</c> ties the call to our request
    /// and no span records a URL that ends in the hash prefix (ADR-0066).
    /// </summary>
    internal static SocketsHttpHandler CreatePrimaryHandler() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        AllowAutoRedirect = false,
        UseCookies = false,
        ActivityHeadersPropagator = null
    };
}
