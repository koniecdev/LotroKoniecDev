using System.Security.Cryptography;
using System.Text.Json;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace LotroKoniecDev.Frontend.Components.Pages.ImportExport;

/// <summary>
/// Carries an import's summary and version across the redirect after the import, so the list loads in a
/// new request with a renewed token: a long import can outlive its token, and the TMS allows no clock skew
/// (#933). The value travels in the query string, protected, so a link someone else wrote can neither put
/// text on the page nor choose the version of the next upload.
/// </summary>
internal sealed class ImportSummaryRedirect
{
    internal const string SummaryParameter = "summary";

    /// <summary>Bump it when the value's shape changes, so links for the old shape are ignored.</summary>
    internal const string Purpose = "LotroKoniecDev.Frontend.ImportSummary.v1";

    private const string PagePath = "/import-export";

    /// <summary>A value with a field missing or null is refused, so the page never reads a null list.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    private readonly IDataProtector _protector;

    public string PageUriWith(CompletedImport completedImport) =>
        QueryHelpers.AddQueryString(
            PagePath,
            SummaryParameter,
            _protector.Protect(JsonSerializer.Serialize(completedImport, JsonOptions)));

    /// <summary>
    /// A value this page did not write, or one whose key or shape is gone, shows no summary.
    /// </summary>
    public CompletedImport? Read(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CompletedImport>(_protector.Unprotect(protectedValue), JsonOptions);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }

    public ImportSummaryRedirect(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    internal sealed record CompletedImport(Guid GameVersionId, ImportSummary Summary);
}
