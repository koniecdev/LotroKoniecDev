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
/// Maps the GDPR data-export download route (LEGAL-02). A Blazor SSR page cannot return a file, so this
/// server route builds the export and sends it with a <c>Content-Disposition</c> attachment header.
/// Since #690 it is a <b>POST</b> that carries the current password, because the export is the one
/// sensitive action that used to ask for nothing. The password is checked by the auth API, not here, so
/// a session alone is never enough (ADR-0052). The confirmation form lives on the
/// <see cref="ExportAccountData"/> page, and a wrong password sends the user back to it with a marker
/// in the query string, because a redirect cannot carry a problem body.
/// The route builds the full Art. 15 document (ADR-0032): the auth part plus the TMS contribution part.
/// A TMS failure only makes the file incomplete (<c>isComplete: false</c>) and does not fail the
/// download.
/// </summary>
internal static class AccountEndpointsExtensions
{
    /// <summary>The page that asks for the password. The account page's export button links here.</summary>
    internal const string ExportPagePath = "/account/export";

    /// <summary>The form target that checks the password and returns the file.</summary>
    internal const string ExportDownloadPath = "/account/export/download";

    /// <summary>The form field the password arrives in.</summary>
    internal const string PasswordFormField = "password";

    /// <summary>Tells the export page which sentence to show after a refused download.</summary>
    internal const string ErrorQueryKey = "error";

    /// <summary>The claim naming the caller, so a refusal can be tied to an account.</summary>
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
            // A form parameter is what makes the framework demand the antiforgery token, so the binding
            // below is part of the protection, not only a convenience.
            endpoints.MapPost(ExportDownloadPath, DownloadAccountExportAsync)
                .RequireAuthorization();

            return endpoints;
        }
    }

    /// <summary>
    /// The route's handler, internal so a unit test can call it without a web host. With the right
    /// password it returns a file with the indented camelCase JSON; with a wrong one it redirects back to
    /// the confirmation page. Only the auth part can fail the download; when the TMS part fails, the file
    /// simply has <c>translationData: null</c> and <c>isComplete: false</c>.
    /// </summary>
    /// <remarks>
    /// The services are marked explicitly. Once one parameter comes from the form, every other one that
    /// is not obviously a service is read as a JSON body, and the endpoint refuses to build at all — so
    /// the attributes are what keep the binding unambiguous.
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
                UserAgent(httpContext),
                null);
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
                UserAgent(httpContext),
                null);

            // A dead session goes back to the page with no marker. The 401 already marked the session
            // dead, so that request signs the user out and sends them to log in, and they return to a
            // clean form — a "session expired" sentence would greet them right after logging in.
            if (result.IsUnauthorized)
            {
                return Results.Redirect(ExportPagePath);
            }

            // A mistyped password and a throttle belong on the page the user just used, so they go back
            // to it with a marker. Everything else keeps answering with the Polish problem body this
            // route has always returned, trace id included (#548, #703, ADR-0044 §5): those are the
            // failures somebody has to diagnose.
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

        // The TMS part is found through the 'contribution-data-export' rel (#610). A rel we cannot
        // resolve is treated exactly like a failed call: we never guess a path, and it never fails the
        // download (ADR-0032).
        // A 200 whose body is empty or does not parse counts as a failed call too, and the HTTP layer
        // decides that (#638, #653). So a success here always carries a value, and anything else leaves
        // translationData null.
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
        // Stamped in Poland time, like every other date the user reads (#736): the file name is what they
        // sort their downloads by, so it has to match the clock they downloaded it on.
        string fileName = string.Format(
            CultureInfo.InvariantCulture,
            "lotro-translator-moje-dane-{0:yyyyMMdd-HHmmss}.json",
            DateTimeOffset.UtcNow.ToPolandTime());

        // The auth API's own line only ever names this service as the caller. This one carries the
        // reader's real address (#690, ADR-0052).
        LogExportDownloaded(
            logger,
            result.Value.AuthData.UserId,
            SensitiveDataRedactor.MaskEmail(result.Value.AuthData.Email),
            ClientIpAddress(httpContext),
            UserAgent(httpContext),
            exportFile.IsComplete,
            null);

        return Results.File(payload, "application/json", fileName);
    }

    /// <summary>
    /// The marker to send the user back to the export page with, or <c>null</c> when the failure is not
    /// theirs to fix. The page shows the matching sentence and a link to a clean form. The password cases
    /// are matched on the API's own <c>errorCode</c> rather than on the bare status, so a validation rule
    /// added later does not silently come out as "wrong password". Two refusals are a 429: the account's
    /// own confirmation budget, which carries a code and a 15-minute window (ADR-0053), and the bare
    /// per-address bucket the account GET sits on, keyed on this reader's own address (ADR-0054), which
    /// clears within a minute. The page says a different sentence for each.
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

    private static readonly Action<ILogger, Guid, string, string?, string, bool, Exception?> LogExportDownloaded =
        LoggerMessage.Define<Guid, string, string?, string, bool>(
            LogLevel.Information,
            new EventId(1, nameof(LogExportDownloaded)),
            "GDPR data export downloaded by user {UserId} ({MaskedEmail}). IP: {IpAddress}, UserAgent: {UserAgent}, complete: {IsComplete}");

    private static readonly Action<ILogger, string?, int?, string, string?, string, Exception?> LogExportRefused =
        LoggerMessage.Define<string?, int?, string, string?, string>(
            LogLevel.Warning,
            new EventId(2, nameof(LogExportRefused)),
            "GDPR data export refused for subject {Subject} with status {Status}: {Reason}. IP: {IpAddress}, UserAgent: {UserAgent}");
}
