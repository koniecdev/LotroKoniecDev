namespace LotroKoniecDev.Application.Abstractions;

public interface IPreflightChecker
{
    Task<bool> RunAllAsync(
        string datPath,
        string versionFilePath);
}
