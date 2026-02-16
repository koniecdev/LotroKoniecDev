namespace LotroKoniecDev.Application.Abstractions;

public interface IFileProvider
{
    public bool Exists(string? path);
}
