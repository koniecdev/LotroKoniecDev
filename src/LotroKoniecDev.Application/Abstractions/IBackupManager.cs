using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Application.Abstractions;

public interface IBackupManager
{
    Result Create(string datPath);
    void Restore(string datPath);
}
