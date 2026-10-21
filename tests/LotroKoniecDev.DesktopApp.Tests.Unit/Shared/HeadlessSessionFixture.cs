using Avalonia.Headless;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Shared;

/// <summary>
/// One headless Avalonia session for every UI test. Avalonia runs one application per process, and the
/// session owns the UI thread the tests dispatch to.
/// </summary>
public sealed class HeadlessSessionFixture : IDisposable
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

    public void Dispose()
    {
        Session.Dispose();
    }
}
