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
