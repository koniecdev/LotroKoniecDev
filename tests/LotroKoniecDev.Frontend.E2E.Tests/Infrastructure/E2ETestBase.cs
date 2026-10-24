using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// Each test gets its own <see cref="IBrowserContext"/>, so cookies never leak between cases. It ignores
/// HTTPS errors, because the stack's certificate is self-signed.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E-Frontend")]
public abstract class E2ETestBase : IAsyncLifetime
{
    /// <summary>
    /// Runs before any page script. A violation only fires an event; the page still loads.
    /// </summary>
    private const string CspViolationListener =
        """
        document.addEventListener('securitypolicyviolation', event =>
            window.__reportCspViolation(
                `${location.origin}${location.pathname}: ${event.effectiveDirective} blocked ${event.blockedURI || 'inline content'}`));
        """;

    private readonly ConcurrentQueue<string> _cspViolations = new();

    protected E2ETestBase(PlaywrightStackFixture fixture)
    {
        Fixture = fixture;
    }

    protected PlaywrightStackFixture Fixture { get; }

    protected IBrowserContext Context { get; private set; } = null!;

    protected IPage Page { get; private set; } = null!;

    /// <summary>
    /// The auth host runs in Testing with its real CSP, and a block is visible only this way (#693).
    /// </summary>
    protected IReadOnlyCollection<string> CspViolations => _cspViolations;

    public async Task InitializeAsync()
    {
        Context = await Fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 }
        });
        Context.SetDefaultTimeout(20_000);
        await Context.ExposeFunctionAsync("__reportCspViolation", (string violation) => _cspViolations.Enqueue(violation));
        await Context.AddInitScriptAsync(CspViolationListener);
        Page = await Context.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        await Context.DisposeAsync();
    }

    protected ILocator ByTestId(string testId) => Page.GetByTestId(testId);
}
