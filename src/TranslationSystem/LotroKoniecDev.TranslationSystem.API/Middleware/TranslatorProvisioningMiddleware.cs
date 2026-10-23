using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth.Provisioning;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Middleware;

/// <summary>
/// Creates the caller's <c>Translator</c> on the first authenticated request (ADR-0004, amended
/// 2026-06-24). Best effort: a <see cref="Result"/> failure is logged and skipped, so read endpoints
/// never depend on it, and the write handlers provision again and report the error. An infrastructure
/// fault is not swallowed.
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
