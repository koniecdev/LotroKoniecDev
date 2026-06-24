using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth.Provisioning;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Middleware;

/// <summary>
/// Eagerly provisions the TMS-local <c>Translator</c> for an authenticated caller on their first
/// authenticated request (ADR-0004 amendment 2026-06-24), so a freshly registered and logged-in user
/// already has a profile before performing any write. Runs only once the request has passed
/// authentication and authorization (it is wired after <c>UseAuthorization</c>), so a 401/403 short-
/// circuits before it and an unauthenticated request is skipped. The provisioner is idempotent and
/// only writes when the claims changed, so steady-state re-touches are a pure read.
///
/// Best-effort by design: a <see cref="Result"/> failure (e.g. a token missing the display-name
/// claim) is logged and skipped so read endpoints never depend on provisioning succeeding — the
/// write handlers keep their own authoritative provisioning, which surfaces the error on a write.
/// A genuine infrastructure fault is not swallowed; it flows through the standard exception pipeline
/// like any other request.
/// </summary>
internal sealed partial class TranslatorProvisioningMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TranslatorProvisioningMiddleware> _logger;

    public TranslatorProvisioningMiddleware(RequestDelegate next, ILogger<TranslatorProvisioningMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ITranslatorProvisioner translatorProvisioner)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            Result<TranslatorId> result = await translatorProvisioner.ProvisionCurrentAsync(context.RequestAborted);
            if (result.IsFailure)
            {
                LogProvisioningSkipped(_logger, result.Error.Code, result.Error.Message);
            }
        }

        await _next(context);
    }

    [LoggerMessage(EventId = EventIds.TranslatorProvisioningSkipped, Level = LogLevel.Warning, Message = "Eager translator provisioning skipped: {Code} {Message}")]
    private static partial void LogProvisioningSkipped(ILogger logger, string code, string message);
}
