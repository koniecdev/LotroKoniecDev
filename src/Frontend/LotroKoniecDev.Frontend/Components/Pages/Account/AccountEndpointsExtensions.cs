using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translators;
using Microsoft.AspNetCore.Mvc;

namespace LotroKoniecDev.Frontend.Components.Pages.Account;

/// <summary>
/// Maps the GDPR data-export download route (LEGAL-02). The account page links here so the export
/// arrives in the browser as a JSON file. A Blazor SSR page cannot return a file, so this server route
/// fetches the export through the same loader the page uses and sends it again with a
/// <c>Content-Disposition</c> attachment header.
/// It is authorized like the auth endpoint behind it, and the caller's session token travels through
/// the typed clients.
/// The route builds the full Art. 15 document (ADR-0032): the auth part plus the TMS contribution part.
/// A TMS failure only makes the file incomplete (<c>isComplete: false</c>) and does not fail the
/// download.
/// </summary>
internal static class AccountEndpointsExtensions
{
    /// <summary>The download URL the account page's export button links to.</summary>
    internal const string ExportDownloadPath = "/account/export";

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
            endpoints.MapGet(ExportDownloadPath, DownloadAccountExportAsync)
                .RequireAuthorization();

            return endpoints;
        }
    }

    /// <summary>
    /// The route's handler, internal so a unit test can call it without a web host. On success it
    /// returns a file with the indented camelCase JSON, and on failure a problem result, either the one
    /// from the API or a 502 of our own. Only the auth part can fail the download; when the TMS part
    /// fails, the file simply has <c>translationData: null</c> and <c>isComplete: false</c>.
    /// </summary>
    internal static async Task<IResult> DownloadAccountExportAsync(
        AccountLoader loader,
        IDiscoveryCache discoveryCache,
        ITranslationSystemClient translationSystemClient,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ApiResult<AccountDataExportResponse> result = await loader.LoadExportAsync(cancellationToken);

        if (result.IsFailure)
        {
            return Results.Problem(ApiProblemCopy.Localize(
                loggerFactory,
                result.ProblemDetails,
                "Nie udało się pobrać danych konta.",
                StatusCodes.Status502BadGateway));
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
        string fileName = string.Format(
            CultureInfo.InvariantCulture,
            "lotro-translator-moje-dane-{0:yyyyMMdd-HHmmss}.json",
            DateTime.UtcNow);

        return Results.File(payload, "application/json", fileName);
    }
}
