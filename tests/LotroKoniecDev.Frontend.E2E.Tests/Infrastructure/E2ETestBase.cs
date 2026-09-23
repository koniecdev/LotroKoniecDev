using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// The base class for every browser flow. Each test gets its own <see cref="IBrowserContext"/>, so
/// cookies and logins never leak between cases, and a page from the shared in-network browser that
/// <see cref="PlaywrightStackFixture"/> connected.
/// The context ignores HTTPS errors, because the stack serves a self-signed certificate. Elements are
/// always found by role, by label or by <c>data-testid</c>.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E-Frontend")]
public abstract class E2ETestBase : IAsyncLifetime
{
    /// <summary>
    /// Runs before any page script, in every page of the context. A violation fires as an event on the
    /// document; the page still loads, so nothing else in a flow would notice it.
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
    /// Every CSP violation a page of this test reported. The auth host runs in Testing, so its real CSP
    /// is on here, and a blocked style or script is visible only this way (#670, #693).
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
