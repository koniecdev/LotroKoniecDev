namespace LotroKoniecDev;

class Program
{
    // Sciezka do katalogu projektu (obok src/)
    static readonly string ProjectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
    static readonly string DataDir = Path.Combine(ProjectDir, "data");

    static int Main(string[] args)
    {
        Console.WriteLine("=== LOTRO Polish Patcher ===\n");
        Console.WriteLine($"Katalog danych: {DataDir}\n");

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        string command = args[0].ToLower();

        // Tryb eksportu
        if (command == "export")
        {
            // Domyslne sciezki: data/client_local_English.dat -> data/exported.txt
            string datPath = args.Length > 1 ? args[1] : Path.Combine(DataDir, "client_local_English.dat");
            string outputPath = args.Length > 2 ? args[2] : Path.Combine(DataDir, "exported.txt");
            return RunExport(datPath, outputPath);
        }

        // Tryb patchowania
        if (command == "patch")
        {
            if (args.Length < 2)
            {
                PrintUsage();
                return 1;
            }
            string translationsPath = args[1];
            string datPath = args.Length > 2 ? args[2] : Path.Combine(DataDir, "client_local_English.dat");
            return RunPatch(translationsPath, datPath);
        }

        PrintUsage();
        return 1;
    }

    static void PrintUsage()
    {
        Console.WriteLine("Uzycie:");
        Console.WriteLine();
        Console.WriteLine("  EKSPORT tekstow z gry (domyslnie z data/):");
        Console.WriteLine("    LotroKoniecDev export [dat_file] [output.txt]");
        Console.WriteLine("    LotroKoniecDev export   <- uzyje data/client_local_English.dat");
        Console.WriteLine();
        Console.WriteLine("  PATCH (wstrzykniecie tlumaczen):");
        Console.WriteLine("    LotroKoniecDev patch <translations.txt> [dat_file]");
        Console.WriteLine();
        Console.WriteLine("Przyklady:");
        Console.WriteLine("  LotroKoniecDev export");
        Console.WriteLine("  LotroKoniecDev patch data/polish.txt");
    }

    static int RunExport(string datPath, string outputPath)
    {
        if (!File.Exists(datPath))
        {
            Console.WriteLine($"BLAD: Plik .dat nie istnieje: {datPath}");
            return 1;
        }

        try
        {
            var exporter = new Exporter();
            exporter.ExportAllTexts(datPath, outputPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BLAD: {ex.Message}");
            return 1;
        }
    }

    static int RunPatch(string translationsPath, string datPath)
    {

        if (!File.Exists(translationsPath))
        {
            Console.WriteLine($"BLAD: Plik tlumaczen nie istnieje: {translationsPath}");
            return 1;
        }

        if (!File.Exists(datPath))
        {
            Console.WriteLine($"BLAD: Plik .dat nie istnieje: {datPath}");
            return 1;
        }

        // Backup
        string backupPath = datPath + ".backup";
        if (!File.Exists(backupPath))
        {
            Console.WriteLine($"Tworzenie backupu: {backupPath}");
            File.Copy(datPath, backupPath);
        }
        else
        {
            Console.WriteLine($"Backup juz istnieje: {backupPath}");
        }

        try
        {
            var patcher = new Patcher();
            int count = patcher.ApplyTranslations(translationsPath, datPath);

            Console.WriteLine();
            Console.WriteLine($"Sukces! Zastosowano {count} tlumaczen.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BLAD: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("Przywracanie z backupu...");

            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, datPath, overwrite: true);
                Console.WriteLine("Przywrocono z backupu.");
            }

            return 1;
        }
    }
}
