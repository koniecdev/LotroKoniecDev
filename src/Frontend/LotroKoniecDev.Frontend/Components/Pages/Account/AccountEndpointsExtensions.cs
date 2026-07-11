using System.Globalization;
using System.Text.Json;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using Microsoft.AspNetCore.Mvc;

namespace LotroKoniecDev.Frontend.Components.Pages.Account;

/// <summary>
/// Maps the GDPR data-export download route (LEGAL-02). The account page links here so the export
/// lands as a JSON <em>file</em> in the browser — a Blazor SSR page cannot return a file result, so
/// this server route fetches the export envelope through the same loader seam the page uses and
/// re-serves it with a <c>Content-Disposition</c> attachment header. Authorized like the upstream
/// auth endpoint; the bearer of the caller's session flows through the typed client.
/// </summary>
internal static class AccountEndpointsExtensions
{
    /// <summary>The download URL — linked from the account page's export action.</summary>
    internal const string ExportDownloadPath = "/account/export";

    private static readonly JsonSerializerOptions ExportSerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
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
    /// problem result (the upstream's, or a 502 fallback) on failure.
    /// </summary>
    internal static async Task<IResult> DownloadAccountExportAsync(
        AccountLoader loader,
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

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(result.Value, ExportSerializerOptions);
        string fileName = string.Format(
            CultureInfo.InvariantCulture,
            "lotro-translator-moje-dane-{0:yyyyMMdd-HHmmss}.json",
            DateTime.UtcNow);

        return Results.File(payload, "application/json", fileName);
    }
}
