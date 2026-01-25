using LotroKoniecDev.Models;

namespace LotroKoniecDev.Parsers;

public static class TranslationFileParser
{
    private const string Separator = "||";

    /// <summary>
    /// Parsuje plik tlumaczen w formacie:
    /// file_id||gossip_id||content||args_order||args_id||approved
    /// </summary>
    public static List<Translation> ParseFile(string filePath)
    {
        var translations = new List<Translation>();

        foreach (var line in File.ReadLines(filePath))
        {
            // Pomijaj puste linie i komentarze
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                continue;

            var translation = ParseLine(line);
            if (translation != null)
            {
                translations.Add(translation);
            }
        }

        // Sortuj po file_id dla optymalizacji I/O
        return translations.OrderBy(t => t.FileId).ThenBy(t => t.GossipId).ToList();
    }

    public static Translation? ParseLine(string line)
    {
        var parts = line.Split(new[] { Separator }, StringSplitOptions.None);

        if (parts.Length < 5)
        {
            Console.WriteLine($"WARN: Nieprawidlowa linia (za malo pol): {line}");
            return null;
        }

        try
        {
            var translation = new Translation
            {
                FileId = int.Parse(parts[0]),
                GossipId = int.Parse(parts[1]),
                Content = parts[2],
                ArgsOrder = ParseArgsArray(parts[3]),
                ArgsId = ParseArgsArray(parts[4])
            };

            return translation;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARN: Blad parsowania linii: {line} - {ex.Message}");
            return null;
        }
    }

    private static int[]? ParseArgsArray(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Equals("NULL", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            // Format: "1-2-3" -> [0, 1, 2] (konwersja z 1-indexed na 0-indexed)
            return value.Split('-')
                        .Select(s => int.Parse(s) - 1)  // 1-indexed -> 0-indexed
                        .ToArray();
        }
        catch
        {
            return null;
        }
    }
}
