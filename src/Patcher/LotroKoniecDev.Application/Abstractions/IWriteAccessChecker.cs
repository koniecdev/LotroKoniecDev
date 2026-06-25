namespace LotroKoniecDev.Application.Abstractions;

public interface IWriteAccessChecker
{
    bool CanWriteTo(string directoryPath);
}
