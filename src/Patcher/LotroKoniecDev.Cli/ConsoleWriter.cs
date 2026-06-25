namespace LotroKoniecDev.Cli;

internal static class ConsoleWriter
{
    public static void WriteInfo(string message) =>
        Console.WriteLine(message);

    public static void WriteSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public static void WriteWarning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"WARN: {message}");
        Console.ResetColor();
    }

    public static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"ERROR: {message}");
        Console.ResetColor();
    }

    public static void WriteProgress(string message) =>
        Console.Write($"\r{message}".PadRight(60));
}
