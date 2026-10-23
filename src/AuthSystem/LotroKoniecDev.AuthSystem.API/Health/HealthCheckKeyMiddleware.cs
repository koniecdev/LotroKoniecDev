using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.AuthSystem.API.Settings;

namespace LotroKoniecDev.AuthSystem.API.Health;

/// <summary>
/// Any request without the key gets 404 before a check runs, so a stranger cannot make this API touch
/// the database, mail server or broker (ADR-0058). SHA-256 digests in constant time, one header value
/// only, as in <see cref="Services.RateLimiting.RateLimitPartitionKeyResolver"/>. With no key set the
/// gate is open. The TMS API has its own copy of this class.
/// </summary>
internal sealed class HealthCheckKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly byte[]? _keyDigest;

    public HealthCheckKeyMiddleware(RequestDelegate next, IOptions<HealthCheckSettings> settings)
    {
        _next = next;
        string? configuredKey = settings.Value.Key;
        _keyDigest = string.IsNullOrWhiteSpace(configuredKey) ? null : Digest(configuredKey);
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (_keyDigest is null || PresentsTheKey(context.Request.Headers, _keyDigest))
        {
            return _next(context);
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    private static bool PresentsTheKey(IHeaderDictionary headers, byte[] keyDigest) =>
        headers[HealthCheckHeaders.Key] is [{ } presentedKey]
        && CryptographicOperations.FixedTimeEquals(Digest(presentedKey), keyDigest);

    private static byte[] Digest(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
