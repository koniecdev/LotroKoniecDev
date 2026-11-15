namespace LotroKoniecDev.Tests.E2E;

/// <summary>
/// One copy of the DAT for every test of one class that leaves it as it found it, so a run does not
/// make a 2 GB copy per test (#1408). It is made at its first use and deleted when the class's last
/// test is done.
/// </summary>
public sealed class SharedDatCopy : IDisposable
{
    private readonly E2ETestFixture _fixture;
    private string? _datPath;

    public string GetDatPath() => _datPath ??= _fixture.CreateTempDatCopy();

    public void Dispose()
    {
        if (_datPath is not null)
        {
            _fixture.DeleteTempDir(Path.GetDirectoryName(_datPath)!);
        }
    }

    public SharedDatCopy(E2ETestFixture fixture)
    {
        _fixture = fixture;
    }
}
