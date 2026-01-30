namespace LotroKoniecDev.CLI.Guards;

public static class ArgsValidator
{
    public static bool IsValid(string[] args)
    {
        return args != null! && args.Length != 0 && args.Length <= 3;
    }
}
