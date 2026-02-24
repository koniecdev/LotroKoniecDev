using static LotroKoniecDev.Cli.ConsoleWriter;

namespace LotroKoniecDev.Cli;

internal sealed class ConsoleStatusReporter : IOperationStatusReporter
{
    public void Report(string message) => WriteInfo(message);
}
