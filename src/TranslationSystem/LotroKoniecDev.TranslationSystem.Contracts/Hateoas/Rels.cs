namespace LotroKoniecDev.TranslationSystem.Contracts.Hateoas;

public static class Rels
{
    public const string Self = "self";

    // Entry points — the vocabulary the discovery document advertises so a client never has to
    // hardcode a path. These names are a public contract: rename one and every client breaks.
    public const string TranslationFile = "translation-file";
    public const string Progress = "progress";
    public const string Translations = "translations";
    public const string TranslationStats = "translation-stats";
    public const string GameVersions = "game-versions";

    /// <summary>
    /// The caller's own contribution export. <b>Load-bearing beyond its endpoint:</b> its target requires
    /// nothing but authentication, so the frontend's <c>DiscoveryCache</c> treats its absence under an
    /// authenticated cache key as proof the bearer never reached the API, and force-signs the session out.
    /// Tightening that endpoint's policy — or renaming this rel — signs every logged-in user out on their
    /// next page load; change the frontend guard in the same commit.
    /// </summary>
    public const string ContributionDataExport = "contribution-data-export";

    // Translation aggregate actions
    public const string Upsert = "upsert";
    public const string Approve = "approve";

    // Translation collection actions
    public const string BulkApprove = "bulk-approve";

    // GameVersion aggregate actions
    public const string Register = "register";
    public const string Delete = "delete";
    public const string Import = "import";

    // Collection / pagination navigation
    public const string FirstPage = "first-page";
    public const string PreviousPage = "previous-page";
    public const string NextPage = "next-page";
    public const string LastPage = "last-page";
}
