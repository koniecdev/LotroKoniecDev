using LotroKoniecDev.Application;

namespace LotroKoniecDev;

public sealed class ConsoleProgressReporter : IProgress<OperationProgress>
{
    public void Report(OperationProgress value)
    {
        Console.WriteLine($"{value.Current}/{value.Total}");
    }
}
