using LotroKoniecDev.Application.Abstractions;

namespace LotroKoniecDev;

internal sealed class FileProvider : IFileProvider
{
    public bool Exists(string? path) => File.Exists(path);
}
