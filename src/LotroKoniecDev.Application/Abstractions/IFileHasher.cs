namespace LotroKoniecDev.Application.Abstractions;

public interface IFileHasher
{
    Result<string> ComputeHash(string filePath);
}
