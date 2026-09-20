using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.Formatting;
using LotroKoniecDev.Frontend.Infrastructure.Hateoas;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translators;
using Microsoft.AspNetCore.Mvc;
using AuthRels = LotroKoniecDev.AuthSystem.Contracts.Hateoas.Rels;

namespace LotroKoniecDev.Frontend.Components.Pages.Account;

/// <summary>
/// Maps the GDPR data-export download route (LEGAL-02). The export page posts the current password
/// here and the export arrives in the browser as a JSON file. A Blazor SSR page cannot return a file,
/// so this server route fetches the export through the same loader the pages use and sends it again
/// with a <c>Content-Disposition</c> attachment header.
/// The auth API checks the password before it hands anything over (#690). A failure goes back to the
/// export page as a short code in the query string, because a form post that ends in a file has no
/// page of its own to show an error on.
/// Every attempt is logged here too: the auth API sees this server's address, and only this route
/// sees the browser's.
/// The route builds the full Art. 15 document (ADR-0032): the auth part plus the TMS contribution part.
/// A TMS failure only makes the file incomplete (<c>isComplete: false</c>) and does not fail the
/// download.
/// </summary>
internal static class AccountEndpointsExtensions
{
    /// <summary>The page that asks for the password. The account page links to it.</summary>
    internal const string ExportPagePath = "/account/export";

    /// <summary>The URL the export page's form posts the password to.</summary>
    internal const string ExportDownloadPath = "/account/export/download";

    internal const string ErrorQueryParameter = "error";
    internal const string PasswordRequiredError = "password-required";
    internal const string InvalidPasswordError = "invalid-password";
    internal const string TooManyRequestsError = "too-many-requests";
    internal const string UnavailableError = "unavailable";
    internal const string FailedError = "failed";

    private const string SubjectClaimType = "sub";

    private static readonly Action<ILogger, string?, bool, string?, string, Exception?> LogExportDownloaded =
        LoggerMessage.Define<string?, bool, string?, string>(
            LogLevel.Information,
            new EventId(1, nameof(LogExportDownloaded)),
            "GDPR export downloaded by user {Subject}; complete: {IsComplete}. IP: {IpAddress}, UserAgent: {UserAgent}");

    private static readonly Action<ILogger, string?, string, string?, string, int?, Exception?> LogExportRefused =
        LoggerMessage.Define<string?, string, string?, string, int?>(
            LogLevel.Warning,
            new EventId(2, nameof(LogExportRefused)),
            "GDPR export refused for user {Subject}: {Reason}. IP: {IpAddress}, UserAgent: {UserAgent}, API status: {ApiStatus}");

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
            // Binding the form field makes the framework check the antiforgery token before the
            // handler runs.
            endpoints.MapPost(
                    ExportDownloadPath,
                    (
                        [FromForm] string? password,
                        HttpContext httpContext,
                        AccountLoader loader,
                        IDiscoveryCache discoveryCache,
                        ITranslationSystemClient translationSystemClient,
                        ILoggerFactory loggerFactory,
                        CancellationToken cancellationToken) => DownloadAccountExportAsync(
                        password,
                        httpContext,
                        loader,
                        discoveryCache,
                        translationSystemClient,
                        loggerFactory,
                        cancellationToken))
                .RequireAuthorization();

            return endpoints;
        }
    }

    /// <summary>
    /// The route's handler, internal so a unit test can call it without a web host. On success it
    /// returns a file with the indented camelCase JSON. On failure it redirects back to the export page
    /// with an error code. Only the auth part can fail the download; when the TMS part fails, the file
    /// simply has <c>translationData: null</c> and <c>isComplete: false</c>.
    /// </summary>
    internal static async Task<IResult> DownloadAccountExportAsync(
        string? password,
        HttpContext httpContext,
        AccountLoader loader,
        IDiscoveryCache discoveryCache,
        ITranslationSystemClient translationSystemClient,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ILogger logger = loggerFactory.CreateLogger(typeof(AccountEndpointsExtensions).FullName!);
        string? subject = httpContext.User.FindFirst(SubjectClaimType)?.Value;
        string? ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();
        string userAgent = httpContext.Request.Headers.UserAgent.ToString();

        if (string.IsNullOrWhiteSpace(password))
        {
            LogExportRefused(logger, subject, PasswordRequiredError, ipAddress, userAgent, null, null);
            return RedirectToExportPage(PasswordRequiredError);
        }

        ApiResult<AccountResponse> account = await loader.LoadAccountAsync(cancellationToken);
        if (account.IsFailure)
        {
            // The export page loads the same resource, so it shows this failure properly, a dead
            // session included.
            LogExportRefused(logger, subject, UnavailableError, ipAddress, userAgent, null, null);
            return RedirectToExportPage(UnavailableError);
        }

        LinkDto? exportLink = account.Value.Links.FindLink(AuthRels.ExportAccountData);
        if (exportLink is null)
        {
            LogExportRefused(logger, subject, UnavailableError, ipAddress, userAgent, null, null);
            return RedirectToExportPage(UnavailableError);
        }

        ApiResult<AccountDataExportResponse> result =
            await loader.ExportAsync(exportLink.Href, password, cancellationToken);

        if (result.IsFailure)
        {
            string error = result.ProblemDetails?.Status switch
            {
                StatusCodes.Status400BadRequest => InvalidPasswordError,
                StatusCodes.Status429TooManyRequests => TooManyRequestsError,
                _ => FailedError
            };

            LogExportRefused(logger, subject, error, ipAddress, userAgent, result.ProblemDetails?.Status, null);
            return RedirectToExportPage(error);
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

        LogExportDownloaded(logger, subject, exportFile.IsComplete, ipAddress, userAgent, null);

        // A personal-data document. Neither the browser nor anything in between may keep a copy.
        httpContext.Response.Headers.CacheControl = "no-store";

        return Results.File(payload, "application/json", fileName);
    }

    private static IResult RedirectToExportPage(string error) =>
        Results.LocalRedirect($"{ExportPagePath}?{ErrorQueryParameter}={error}");
}
