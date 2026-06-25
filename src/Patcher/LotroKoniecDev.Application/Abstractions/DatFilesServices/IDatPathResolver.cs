namespace LotroKoniecDev.Application.Abstractions.DatFilesServices;

public interface IDatPathResolver
{
    string? Resolve(string? explicitPath);
}
