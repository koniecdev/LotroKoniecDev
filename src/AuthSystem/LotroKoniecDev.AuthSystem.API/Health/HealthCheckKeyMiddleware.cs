using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.AuthSystem.API.Settings;

namespace LotroKoniecDev.AuthSystem.API.Health;

/// <summary>
/// Lets a request into the full /health only when it carries the health check key (ADR-0058, #853).
/// Any other request gets 404 before a single check runs, so a stranger cannot make this API query the
/// database, connect to the mail server or open a broker connection. The keys are compared as SHA-256
/// digests in constant time, and exactly one header value is accepted, as in
/// <see cref="Services.RateLimiting.RateLimitPartitionKeyResolver"/>. With no key set the gate is open;
/// <see cref="HealthCheckSettingsValidator"/> allows that only in Development and Testing.
/// The TMS API has its own copy of this class.
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
