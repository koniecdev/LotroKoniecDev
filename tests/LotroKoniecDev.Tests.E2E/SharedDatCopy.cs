namespace LotroKoniecDev.Tests.E2E;

/// <summary>
/// One copy of the DAT for every test of one class that leaves it as it found it, so a run does not
/// make a 2 GB copy per test (#1408). It is made at its first use and deleted when the class's last
/// test is done. A failed copy is not tried again: every later test gets the same exception.
/// </summary>
public sealed class SharedDatCopy : IDisposable
{
    private readonly E2ETestFixture _fixture;
    private readonly Lazy<string> _datPath;

    public string GetDatPath() => _datPath.Value;

    public void Dispose()
    {
        if (_datPath.IsValueCreated)
        {
            _fixture.DeleteTempDir(Path.GetDirectoryName(_datPath.Value)!);
        }
    }

    public SharedDatCopy(E2ETestFixture fixture)
    {
        _fixture = fixture;
        _datPath = new Lazy<string>(fixture.CreateTempDatCopy);
    }
}
