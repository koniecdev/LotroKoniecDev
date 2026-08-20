namespace LotroKoniecDev.TranslationSystem.Contracts.Hateoas;

public static class Rels
{
    public const string Self = "self";

    // Entry points. The discovery document advertises these names so a client never has to hardcode
    // a path. The names are a public contract: rename one and every client breaks.
    public const string TranslationFile = "translation-file";
    public const string Progress = "progress";
    public const string Translations = "translations";
    public const string TranslationStats = "translation-stats";
    public const string GameVersions = "game-versions";

    /// <summary>
    /// The caller's own contribution export. <b>This rel does more than name an endpoint.</b> Its
    /// target asks for nothing but a logged-in user, so when the frontend's <c>DiscoveryCache</c> does
    /// not see it under an authenticated cache key, it concludes the token never reached the API and
    /// signs the session out. If you tighten that endpoint's policy, or rename this rel, every
    /// logged-in user is signed out on their next page load. Change the frontend guard in the same
    /// commit.
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
