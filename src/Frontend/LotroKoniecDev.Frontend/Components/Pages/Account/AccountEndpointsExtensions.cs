using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Infrastructure.Formatting;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Logging.Redaction;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translators;
using Microsoft.AspNetCore.Mvc;

namespace LotroKoniecDev.Frontend.Components.Pages.Account;

/// <summary>
/// The GDPR data-export download route (LEGAL-02). A Blazor SSR page cannot return a file, so a server
/// route sends it. It is a POST that carries the current password (#690), and the auth API checks it, so
/// a session alone is never enough (ADR-0052). A refused password sends the user back to
/// <see cref="ExportAccountData"/> with a query-string marker, because a redirect cannot carry a problem
/// body. The file is the full Art. 15 document (ADR-0032); a TMS failure only makes it incomplete.
/// </summary>
internal static partial class AccountEndpointsExtensions
{
    /// <summary>The account page's export button links here.</summary>
    internal const string ExportPagePath = "/account/export";

    internal const string ExportDownloadPath = "/account/export/download";

    internal const string PasswordFormField = "password";

    /// <summary>Tells the export page which sentence to show after a refused download.</summary>
    internal const string ErrorQueryKey = "error";

    private const string SubjectClaimType = "sub";

    internal const string PasswordErrorCode = "password";
    internal const string PasswordRequiredErrorCode = "required";
    internal const string ThrottledErrorCode = "throttled";
    internal const string PasswordThrottledErrorCode = "password-throttled";

    private static readonly JsonSerializerOptions ExportSerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    extension(IEndpointRouteBuilder endpoints)
    {
        public IEndpointRouteBuilder MapAccountEndpoints()
        {
            // The handler's form parameter is what makes the framework demand the antiforgery token, so
            // that binding is part of the protection.
            endpoints.MapPost(ExportDownloadPath, DownloadAccountExportAsync)
                .RequireAuthorization();

            return endpoints;
        }
    }

    /// <summary>
    /// Internal so a unit test can call it without a web host. Only the auth part can fail the download;
    /// a TMS failure leaves <c>translationData: null</c> and <c>isComplete: false</c>.
    /// </summary>
    /// <remarks>
    /// The services are marked explicitly: once one parameter comes from the form, any other parameter
    /// that is not obviously a service is read as a JSON body, and the endpoint refuses to build.
    /// </remarks>
    internal static async Task<IResult> DownloadAccountExportAsync(
        [FromForm(Name = PasswordFormField)] string? password,
        HttpContext httpContext,
        [FromServices] AccountLoader loader,
        [FromServices] IDiscoveryCache discoveryCache,
        [FromServices] ITranslationSystemClient translationSystemClient,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ILogger logger = loggerFactory.CreateLogger(typeof(AccountEndpointsExtensions).FullName!);

        if (string.IsNullOrWhiteSpace(password))
        {
            LogExportRefused(
                logger,
                SubjectId(httpContext),
                null,
                "no password was sent",
                ClientIpAddress(httpContext),
                UserAgent(httpContext));
            return RedirectToExportPage(PasswordRequiredErrorCode);
        }

        ApiResult<AccountDataExportResponse> result =
            await loader.DownloadExportAsync(password, cancellationToken);

        if (result.IsFailure)
        {
            LogExportRefused(
                logger,
                SubjectId(httpContext),
                result.ProblemDetails?.Status,
                RefusalReasonFor(result),
                ClientIpAddress(httpContext),
                UserAgent(httpContext));

            // No marker here: the 401 already marked the session dead, so that request signs the user out
            // and they return to a clean form. A "session expired" sentence would greet them after login.
            if (result.IsUnauthorized)
            {
                return Results.Redirect(ExportPagePath);
            }

            // A wrong password and a throttle go back to the form with a marker. Everything else answers
            // with the Polish problem body, trace id included, because somebody has to diagnose those
            // (ADR-0044 §5).
            string? formError = FormErrorFor(result);
            return formError is null
                ? Results.Problem(ApiProblemCopy.Localize(
                    loggerFactory,
                    result.ProblemDetails,
                    "Nie udało się pobrać danych konta.",
                    StatusCodes.Status502BadGateway))
                : RedirectToExportPage(formError);
        }

        TranslatorDataExportResponse? translationData = null;

        // A rel we cannot resolve counts as a failed call: we never guess a path (#610), and it never
        // fails the download (ADR-0032). An empty or unreadable 200 is a failure too (#638), so a success
        // here always carries a value.
        ApiResult<string> contributionHref = await discoveryCache.ResolveTranslationSystemHrefAsync(
            Rels.ContributionDataExport,
            cancellationToken);

        if (contributionHref.IsSuccess)
        {
            ApiResult<TranslatorDataExportResponse> contributionResult =
                await translationSystemClient.GetApiResultAsync<TranslatorDataExportResponse>(
                    contributionHref.Value,
                    cancellationToken);

            if (contributionResult.IsSuccess)
            {
                translationData = contributionResult.Value;
            }
        }

        AccountDataExportFile exportFile = new(
            result.Value.AuthData,
            translationData,
            IsComplete: translationData is not null && result.Value.IsComplete);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(exportFile, ExportSerializerOptions);
        // Poland time, like every date the user reads (#736): they sort their downloads by this name.
        string fileName = string.Format(
            CultureInfo.InvariantCulture,
            "lotro-translator-moje-dane-{0:yyyyMMdd-HHmmss}.json",
            DateTimeOffset.UtcNow.ToPolandTime());

        // The auth API's own log line only sees this service as the caller; this one has the reader's
        // real address (ADR-0052).
        LogExportDownloaded(
            logger,
            result.Value.AuthData.UserId,
            SensitiveDataRedactor.MaskEmail(result.Value.AuthData.Email),
            ClientIpAddress(httpContext),
            UserAgent(httpContext),
            exportFile.IsComplete);

        return Results.File(payload, "application/json", fileName);
    }

    /// <summary>
    /// The marker for the export page, or <c>null</c> when the failure is not the user's to fix. Password
    /// cases match on the <c>errorCode</c>, not the status, so a later validation rule never reads as
    /// "wrong password". Two refusals are a 429: the account's confirmation budget, with a code and a
    /// 15-minute window (ADR-0053), and the bare per-address limit (ADR-0054), which clears within a
    /// minute. The page says a different sentence for each.
    /// </summary>
    private static string? FormErrorFor(ApiResult result) =>
        ErrorCodeOf(result) switch
        {
            ApiProblemCopy.PasswordConfirmationThrottledCode => PasswordThrottledErrorCode,
            ApiProblemCopy.InvalidCurrentPasswordCode => PasswordErrorCode,
            ApiProblemCopy.ExportPasswordRequiredCode => PasswordRequiredErrorCode,
            _ when result.ProblemDetails?.Status is StatusCodes.Status429TooManyRequests => ThrottledErrorCode,
            _ => null
        };

    /// <summary>
    /// What the audit line says went wrong: the API's own error code when it sent one, otherwise only
    /// that the call failed. The status is logged next to it, so an outage and a typo read differently.
    /// </summary>
    private static string RefusalReasonFor(ApiResult result) =>
        ErrorCodeOf(result) ?? "the auth API call failed";

    private static string? ErrorCodeOf(ApiResult result) =>
        result.ProblemDetails is null ? null : ApiProblemCopy.ReadErrorCode(result.ProblemDetails);

    private static string? SubjectId(HttpContext httpContext) =>
        httpContext.User.FindFirst(SubjectClaimType)?.Value;

    private static IResult RedirectToExportPage(string errorCode) =>
        Results.Redirect($"{ExportPagePath}?{ErrorQueryKey}={errorCode}");

    private static string? ClientIpAddress(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString();

    private static string UserAgent(HttpContext httpContext) =>
        httpContext.Request.Headers.UserAgent.ToString();

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "GDPR data export downloaded by user {UserId} ({MaskedEmail}). IP: {IpAddress}, UserAgent: {UserAgent}, complete: {IsComplete}")]
    private static partial void LogExportDownloaded(
        ILogger logger, Guid userId, string maskedEmail, string? ipAddress, string userAgent, bool isComplete);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "GDPR data export refused for subject {Subject} with status {Status}: {Reason}. IP: {IpAddress}, UserAgent: {UserAgent}")]
    private static partial void LogExportRefused(
        ILogger logger, string? subject, int? status, string reason, string? ipAddress, string userAgent);
}
