using LotroKoniecDev.DesktopApp.Presentation;
using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Shared;

/// <summary>The files that "exist" in a test. Paths compare exactly, as a test writes them.</summary>
internal sealed class InMemoryFileProbe : IFileProbe
{
    private readonly HashSet<string> _files = new(StringComparer.Ordinal);

    public bool FileExists(string path) => _files.Contains(path);

    public void Add(string path) => _files.Add(path);

    public void AddGame(string folder) => Add(Path.Combine(folder, DatFileConstants.DatFileName));

    public void Remove(string path) => _files.Remove(path);
}
