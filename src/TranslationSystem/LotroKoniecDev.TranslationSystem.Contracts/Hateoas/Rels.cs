namespace LotroKoniecDev.TranslationSystem.Contracts.Hateoas;

public static class Rels
{
    public const string Self = "self";

    // Translation aggregate actions
    public const string Upsert = "upsert";
    public const string Approve = "approve";

    // GameVersion aggregate actions
    public const string Register = "register";
    public const string Delete = "delete";

    // Collection / pagination navigation
    public const string FirstPage = "first-page";
    public const string PreviousPage = "previous-page";
    public const string NextPage = "next-page";
    public const string LastPage = "last-page";
}
