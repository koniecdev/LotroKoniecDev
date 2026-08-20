using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth.Provisioning;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Middleware;

/// <summary>
/// Creates the TMS-side <c>Translator</c> for an authenticated caller on their first authenticated
/// request (ADR-0004, amended 2026-06-24), so a user who just registered and logged in already has a
/// profile before any write.
/// It runs only after the request passed authentication and authorization, because it is wired after
/// <c>UseAuthorization</c>, so a 401 or 403 stops earlier and an anonymous request skips it. The
/// provisioner is safe to call twice and only writes when the claims changed, so a normal request is
/// just a read.
///
/// It only tries its best. A <see cref="Result"/> failure, for example a token with no display-name
/// claim, is logged and skipped, so read endpoints never depend on provisioning working. The write
/// handlers do their own provisioning and report the error there.
/// A real infrastructure fault is not swallowed: it goes through the normal exception pipeline like any
/// other request.
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
