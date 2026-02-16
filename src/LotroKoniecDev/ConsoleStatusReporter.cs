using LotroKoniecDev.Application;
using static LotroKoniecDev.ConsoleWriter;

namespace LotroKoniecDev;

internal sealed class ConsoleStatusReporter : IOperationStatusReporter
{
    public void Report(string message) => WriteInfo(message);
}
