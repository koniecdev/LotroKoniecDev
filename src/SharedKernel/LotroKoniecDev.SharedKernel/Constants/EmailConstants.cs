namespace LotroKoniecDev.SharedKernel.Constants;

public static class EmailConstants
{
    public const int MinLength = 5;
    public const int MaxLength = 250;
    public const string RegexPattern =
        @"\A(?!.*\s)(?:[A-Za-z0-9_%+-]+(?:\.[A-Za-z0-9_%+-]+)*)@(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(?:\.(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?))+\z";
}
