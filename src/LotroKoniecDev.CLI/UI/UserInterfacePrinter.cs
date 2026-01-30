namespace LotroKoniecDev.CLI.UI;

internal static class UserInterfacePrinter
{
    public static void PrintBanner()
    {
        Console.WriteLine("=== LOTRO Translations Patcher ===");
        Console.WriteLine();
    }
    
    public static void PrintManual()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine();
        Console.WriteLine("  EXPORT texts from game:");
        Console.WriteLine("    LotroKoniecDev export [dat_file] [output.txt]");
        Console.WriteLine();
        Console.WriteLine("  PATCH (inject translations):");
        Console.WriteLine("    LotroKoniecDev patch <name> [dat_file]");
        Console.WriteLine("    Name resolves to translations/<name>.txt");
        Console.WriteLine();
        Console.WriteLine("If no DAT file is specified, LOTRO installation is detected automatically.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  LotroKoniecDev patch example_polish");
        Console.WriteLine(@"  LotroKoniecDev patch example_polish C:\path\to\client_local_English.dat");
        Console.WriteLine("  LotroKoniecDev export");
    }
}
