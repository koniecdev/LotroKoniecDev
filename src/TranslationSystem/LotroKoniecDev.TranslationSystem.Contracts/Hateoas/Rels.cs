namespace LotroKoniecDev.TranslationSystem.Contracts.Hateoas;

public static class Rels
{
    public const string Self = "self";

    // Entry points, advertised by the discovery document. The names are a public contract (ADR-0041):
    // rename one and every client breaks.
    public const string TranslationFile = "translation-file";
    public const string Progress = "progress";
    public const string Translations = "translations";
    public const string TranslationStats = "translation-stats";
    public const string GameVersions = "game-versions";

    /// <summary>
    /// Also the frontend's signed-in marker: its target needs only a login, so when
    /// <c>DiscoveryCache</c> does not see this rel for a signed-in user, it signs the session out.
    /// Tighten that endpoint's policy or rename this rel, and every user is signed out on the next page
    /// load. Change the frontend guard in the same commit.
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
