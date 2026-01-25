using LotroKoniecDev.Models;

namespace LotroKoniecDev;

public class Exporter
{
    private const string Separator = "<--DO_NOT_TOUCH!-->";

    /// <summary>
    /// Eksportuje teksty w formacie GOTOWYM DO TLUMACZENIA (6 pol)
    /// file_id||gossip_id||text||args_order||args_id||approved
    /// </summary>
    public void ExportAllTexts(string datFilePath, string outputPath)
    {
        Console.WriteLine($"Otwieranie: {datFilePath}");
        int handle = DatExport.OpenDatFile(datFilePath);
        if (handle < 0)
            throw new Exception($"Nie mozna otworzyc: {datFilePath}");

        try
        {
            var fileSizes = DatExport.GetAllSubfileSizes(handle);
            Console.WriteLine($"Znaleziono {fileSizes.Count} subplikow");

            int textFilesTotal = fileSizes.Count(kv => SubFile.IsTextFile(kv.Key));
            Console.WriteLine($"W tym {textFilesTotal} plikow tekstowych");

            using var writer = new StreamWriter(outputPath, false, System.Text.Encoding.UTF8);
            writer.WriteLine("# LOTRO Text Export - GOTOWY DO TLUMACZENIA");
            writer.WriteLine("# Format: file_id||gossip_id||text||args_order||args_id||approved");
            writer.WriteLine("#");
            writer.WriteLine("# Jak tlumaczysz:");
            writer.WriteLine("#   1. Zamien angielski tekst na polski");
            writer.WriteLine("#   2. NIE RUSZAJ <--DO_NOT_TOUCH!--> - to placeholdery na zmienne");
            writer.WriteLine("#   3. args_order/args_id - zostaw NULL chyba ze zmieniasz kolejnosc argumentow");
            writer.WriteLine("#   4. Usun linie ktorych nie tlumaczysz (lub zostaw - beda zignorowane jesli identyczne)");
            writer.WriteLine("#");

            int textFileCount = 0;
            int fragmentCount = 0;

            foreach (var (fileId, (size, _)) in fileSizes)
            {
                if (!SubFile.IsTextFile(fileId)) continue;

                try
                {
                    byte[] data = DatExport.GetSubfileDataBytes(handle, fileId, size);
                    var subFile = new SubFile();
                    subFile.Parse(data);

                    foreach (var (fragmentId, fragment) in subFile.Fragments)
                    {
                        string text = string.Join(Separator, fragment.Pieces);

                        // Escapuj znaki nowej linii i pipe
                        text = text.Replace("\r", "\\r").Replace("\n", "\\n");

                        // Generuj args_order i args_id jesli sa argumenty
                        string argsOrder = "NULL";
                        string argsId = "NULL";

                        if (fragment.ArgRefs.Count > 0)
                        {
                            // Domyslna kolejnosc: 1-2-3-...
                            var order = Enumerable.Range(1, fragment.ArgRefs.Count).Select(i => i.ToString());
                            argsOrder = string.Join("-", order);
                            argsId = argsOrder; // domyslnie to samo
                        }

                        // Format gotowy do importu: file_id||gossip_id||text||args_order||args_id||1
                        writer.WriteLine($"{fileId}||{fragmentId}||{text}||{argsOrder}||{argsId}||1");
                        fragmentCount++;
                    }

                    textFileCount++;
                    if (textFileCount % 500 == 0)
                    {
                        double percent = (double)textFileCount / textFilesTotal * 100;
                        Console.WriteLine($"Przetworzono {textFileCount}/{textFilesTotal} plikow ({percent:F1}%), {fragmentCount} fragmentow...");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WARN: Blad pliku {fileId}: {ex.Message}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"=== GOTOWE ===");
            Console.WriteLine($"Wyeksportowano {fragmentCount:N0} tekstow z {textFileCount:N0} plikow");
            Console.WriteLine($"Zapisano do: {outputPath}");
            Console.WriteLine();
            Console.WriteLine($"Nastepny krok: Przetlumacz teksty i uruchom:");
            Console.WriteLine($"  dotnet run -- patch {outputPath} <sciezka_do_dat>");
        }
        finally
        {
            DatExport.CloseDatFile(handle);
        }
    }
}
