using Microsoft.Playwright;

namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// Base for every browser flow. Owns a per-test <see cref="IBrowserContext"/> (isolated cookies, so
/// auth never leaks between cases) and a page off the shared in-network browser the
/// <see cref="PlaywrightStackFixture"/> connected. The context ignores HTTPS errors — the stack
/// serves a self-signed e2e cert. All element lookups go through role/label or <c>data-testid</c>.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E-Frontend")]
public abstract class E2ETestBase : IAsyncLifetime
{
    protected E2ETestBase(PlaywrightStackFixture fixture)
    {
        Fixture = fixture;
    }

    protected PlaywrightStackFixture Fixture { get; }

    protected IBrowserContext Context { get; private set; } = null!;

    protected IPage Page { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Context = await Fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 }
        });
        Context.SetDefaultTimeout(20_000);
        Page = await Context.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        await Context.DisposeAsync();
    }

    protected ILocator ByTestId(string testId) => Page.GetByTestId(testId);
}
