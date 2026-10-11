using System.Security.Cryptography;
using System.Text.Json;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace LotroKoniecDev.Frontend.Components.Pages.ImportExport;

/// <summary>
/// Carries an import's summary across the redirect after the import (#933). The page redirects so that
/// the version list loads in a new request with a renewed token: the token of the import request may
/// already be dead when an import of several minutes ends, and the TMS allows no clock skew.
/// The summary travels in the query string, so the server keeps no state per user, like the other
/// redirects after a post (#321). It holds the server's notes as text, so it is protected: a link that
/// someone else wrote cannot put text on this page.
/// </summary>
internal sealed class ImportSummaryRedirect
{
    internal const string SummaryParameter = "summary";

    private const string PagePath = "/import-export";

    /// <summary>A new version here makes the page ignore links written for an older shape of the summary.</summary>
    private const string Purpose = "LotroKoniecDev.Frontend.ImportSummary.v1";

    private readonly IDataProtector _protector;

    public string PageUriWith(ImportSummary summary) =>
        QueryHelpers.AddQueryString(
            PagePath,
            SummaryParameter,
            _protector.Protect(JsonSerializer.Serialize(summary)));

    /// <summary>
    /// A value this page did not write, or one written with a key the key ring no longer has, shows no
    /// summary at all.
    /// </summary>
    public ImportSummary? Read(string? protectedSummary)
    {
        if (string.IsNullOrEmpty(protectedSummary))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ImportSummary>(_protector.Unprotect(protectedSummary));
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public ImportSummaryRedirect(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }
}
