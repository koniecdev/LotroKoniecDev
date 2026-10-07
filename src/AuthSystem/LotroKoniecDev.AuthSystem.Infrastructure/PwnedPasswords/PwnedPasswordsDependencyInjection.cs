using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;

internal static class PwnedPasswordsDependencyInjection
{
    /// <summary>
    /// The documented host of the range endpoint. It needs no API key and has no rate limit (checked at
    /// haveibeenpwned.com/API/v3 on 2026-10-07).
    /// </summary>
    internal static readonly Uri BaseAddress = new("https://api.pwnedpasswords.com/");

    /// <summary>
    /// Short on purpose: a person waits on the form, and the endpoint normally answers in well under a
    /// second. After this the password goes through unchecked (ADR-0065).
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

            // No resilience handler: a retry would only make the person wait longer for an answer the
            // policy can do without.
            services.AddHttpClient<IPwnedPasswordChecker, PwnedPasswordChecker>(client =>
                {
                    client.BaseAddress = BaseAddress;
                    client.Timeout = RequestTimeout;
                    client.MaxResponseContentBufferSize = MaxResponseContentBytes;
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("LotroKoniecDev-AuthSystem/1.0");
                })
                .ConfigurePrimaryHttpMessageHandler(CreatePrimaryHandler)
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
                // The factory's own handlers log every request URL at Information, and this URL ends in
                // the hash prefix. The checker's warnings say everything an operator needs (ADR-0065).
                .RemoveAllLoggers();

            return services;
        }
    }

    /// <summary>
    /// No redirects, so a request cannot be walked off the one host we chose, and no cookies, because one
    /// handler serves every request and the API needs none. The pooled connection lifetime is what keeps
    /// DNS fresh now that the handler is never rotated.
    /// </summary>
    internal static SocketsHttpHandler CreatePrimaryHandler() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        AllowAutoRedirect = false,
        UseCookies = false
    };
}
