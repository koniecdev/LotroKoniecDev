using LotroKoniecDev.Models;
using LotroKoniecDev.Parsers;

namespace LotroKoniecDev;

public class Patcher
{
    public int ApplyTranslations(string translationsPath, string datFilePath)
    {
        Console.WriteLine($"Ladowanie tlumaczen z: {translationsPath}");
        var translations = TranslationFileParser.ParseFile(translationsPath);
        Console.WriteLine($"Zaladowano {translations.Count} tlumaczen");

        if (translations.Count == 0)
        {
            Console.WriteLine("Brak tlumaczen do zastosowania");
            return 0;
        }

        Console.WriteLine($"Otwieranie pliku: {datFilePath}");
        int handle = DatExport.OpenDatFile(datFilePath);
        if (handle < 0)
        {
            throw new Exception($"Nie mozna otworzyc pliku .dat: {datFilePath}");
        }

        try
        {
            var fileSizes = DatExport.GetAllSubfileSizes(handle);
            Console.WriteLine($"Znaleziono {fileSizes.Count} subplikow w .dat");

            int patchedCount = 0;
            int currentFileId = -1;
            SubFile? currentSubFile = null;

            foreach (var translation in translations)
            {
                // Sprawdz czy plik istnieje w .dat
                if (!fileSizes.ContainsKey(translation.FileId))
                {
                    Console.WriteLine($"WARN: Plik {translation.FileId} nie istnieje w .dat");
                    continue;
                }

                // Sprawdz czy to plik tekstowy
                if (!SubFile.IsTextFile(translation.FileId))
                {
                    Console.WriteLine($"WARN: Plik {translation.FileId} nie jest plikiem tekstowym");
                    continue;
                }

                // Nowy file_id - zapisz poprzedni i wczytaj nowy
                if (translation.FileId != currentFileId)
                {
                    // Zapisz poprzedni subplik
                    if (currentSubFile != null && currentFileId != -1)
                    {
                        SaveSubFile(handle, currentFileId, currentSubFile, fileSizes[currentFileId]);
                    }

                    // Wczytaj nowy subplik
                    currentFileId = translation.FileId;
                    var (size, _) = fileSizes[currentFileId];

                    try
                    {
                        byte[] data = DatExport.GetSubfileDataBytes(handle, currentFileId, size);
                        currentSubFile = new SubFile { Version = DatExport.GetSubfileVersion(handle, currentFileId) };
                        currentSubFile.Parse(data);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"WARN: Nie mozna wczytac pliku {currentFileId}: {ex.Message}");
                        currentSubFile = null;
                        currentFileId = -1;
                        continue;
                    }
                }

                // Zastosuj tlumaczenie
                if (currentSubFile != null)
                {
                    ulong fragmentId = (ulong)translation.GossipId;
                    if (currentSubFile.Fragments.TryGetValue(fragmentId, out var fragment))
                    {
                        fragment.Pieces = translation.GetPieces().ToList();
                        patchedCount++;

                        if (patchedCount % 100 == 0)
                        {
                            Console.WriteLine($"Patchowanie... {patchedCount}/{translations.Count}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"WARN: Fragment {translation.GossipId} nie istnieje w pliku {translation.FileId}");
                    }
                }
            }

            // Zapisz ostatni subplik
            if (currentSubFile != null && currentFileId != -1)
            {
                SaveSubFile(handle, currentFileId, currentSubFile, fileSizes[currentFileId]);
            }

            DatExport.Flush(handle);
            Console.WriteLine($"Zastosowano {patchedCount} tlumaczen");
            return patchedCount;
        }
        finally
        {
            DatExport.CloseDatFile(handle);
        }
    }

    private void SaveSubFile(int handle, int fileId, SubFile subFile,
                              (int size, int iteration) fileInfo)
    {
        byte[] data = subFile.Serialize();
        DatExport.PutSubfileDataBytes(handle, fileId, data, subFile.Version, fileInfo.iteration);
    }
}
