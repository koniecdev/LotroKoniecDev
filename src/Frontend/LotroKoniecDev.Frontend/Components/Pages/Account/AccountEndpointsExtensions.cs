using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Translators;
using Microsoft.AspNetCore.Mvc;

namespace LotroKoniecDev.Frontend.Components.Pages.Account;

/// <summary>
/// Maps the GDPR data-export download route (LEGAL-02). The account page links here so the export
/// lands as a JSON <em>file</em> in the browser — a Blazor SSR page cannot return a file result, so
/// this server route fetches the export envelope through the same loader seam the page uses and
/// re-serves it with a <c>Content-Disposition</c> attachment header. Authorized like the upstream
/// auth endpoint; the bearer of the caller's session flows through the typed clients. The route
/// composes the full Art. 15 document (ADR-0032): the auth leg plus the TMS contribution leg —
/// a TMS failure degrades the file (<c>isComplete: false</c>) instead of failing the download.
/// </summary>
internal static class AccountEndpointsExtensions
{
    /// <summary>The download URL — linked from the account page's export action.</summary>
    internal const string ExportDownloadPath = "/account/export";

    /// <summary>The TMS leg — the caller's contribution export (self-only; LEGAL-07).</summary>
    internal const string ContributionExportApiPath = "/api/v1/translators/me/data-export";

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
    /// The download route's request delegate, exposed internally so it can be unit-tested without a
    /// web host: it returns a file result with the indented camelCase JSON dump on success, or a
    /// problem result (the upstream's, or a 502 fallback) on failure. Only the auth leg can fail the
    /// download; the TMS leg degrades to <c>translationData: null</c> + <c>isComplete: false</c>.
    /// </summary>
    internal static async Task<IResult> DownloadAccountExportAsync(
        AccountLoader loader,
        ITranslationSystemClient translationSystemClient,
        CancellationToken cancellationToken)
    {
        ApiResult<AccountDataExportResponse> result = await loader.LoadExportAsync(cancellationToken);

        if (result.IsFailure)
        {
            return Results.Problem(result.ProblemDetails ?? new ProblemDetails
            {
                Title = "Nie udało się pobrać danych konta.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        TranslatorDataExportResponse? translationData = null;
        try
        {
            ApiResult<TranslatorDataExportResponse> contributionResult =
                await translationSystemClient.GetApiResultAsync<TranslatorDataExportResponse>(
                    ContributionExportApiPath,
                    cancellationToken);

            if (contributionResult.IsSuccess)
            {
                translationData = contributionResult.Value;
            }
        }
        catch (JsonException)
        {
            // A 200 whose body doesn't parse is still a failed TMS leg — degrade, don't fail
            // the download (ADR-0032). The null-check above likewise covers a 200 empty body.
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
