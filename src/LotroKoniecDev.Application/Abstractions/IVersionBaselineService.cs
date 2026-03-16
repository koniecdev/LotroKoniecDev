namespace LotroKoniecDev.Application.Abstractions;

public interface IVersionBaselineService
{
    Task<Result> SaveBaselineAsync(string datFilePath, string translationFilePath, string versionFilePath);
}
