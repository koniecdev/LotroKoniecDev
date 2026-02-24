using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Cli;

internal interface IBackupManager
{
    Result Create(string datPath);
    void Restore(string datPath);
}
