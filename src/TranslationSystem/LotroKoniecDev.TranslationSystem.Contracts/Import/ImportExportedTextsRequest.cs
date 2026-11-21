using LotroKoniecDev.TranslationSystem.Contracts.Common;

namespace LotroKoniecDev.TranslationSystem.Contracts.Import;

/// <summary>
/// The import's query option: the admin's choice for this one upload, which the server cannot put in the
/// <c>import</c> link. One record for the API and the website, so the option name is written once (#1478).
/// </summary>
public sealed record ImportExportedTextsRequest(bool AllowMassRemoval = false)
{
    public IReadOnlyList<KeyValuePair<string, string?>> ToQueryParameters()
        => AllowMassRemoval
            ? [QueryParameter.Of(nameof(AllowMassRemoval), "true")]
            : [];
}
