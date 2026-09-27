using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// Counts every POST a page sends. <see cref="HoldAnswersAsync"/> also holds the answer to each POST to
/// one path for a moment after the server sent it, as a slow connection would. A second click then lands
/// while the first request is still open, whatever the speed of this machine (#871).
/// A route that fails is recorded, not thrown: a route handler has no caller that could catch it. The
/// request then goes on to the server unheld, so the page still ends and the test can report the record
/// instead of a locator timeout 30 seconds later.
/// </summary>
internal sealed class PostWatch
{
    private readonly ConcurrentQueue<string> _postedPaths = new();
    private readonly ConcurrentQueue<string> _routeFailures = new();

    private PostWatch()
    {
    }

    /// <summary>
    /// Each held request that could not be answered. Without the fix this is expected: the browser drops
    /// the first request when a second click starts a new one. So check the POST count first.
    /// </summary>
    public IReadOnlyCollection<string> RouteFailures => _routeFailures;

    public int PostsTo(string path) => _postedPaths.Count(postedPath => postedPath == path);

    public static PostWatch StartCounting(IPage page)
    {
        PostWatch watch = new();
        page.Request += (_, request) =>
        {
            if (request.Method == "POST")
            {
                watch._postedPaths.Enqueue(new Uri(request.Url).AbsolutePath);
            }
        };
        return watch;
    }

    public static async Task<PostWatch> HoldAnswersAsync(IPage page, string heldPath, TimeSpan answerDelay)
    {
        PostWatch watch = StartCounting(page);
        await page.RouteAsync($"**{heldPath}**", async route =>
        {
            if (route.Request.Method != "POST")
            {
                await route.ContinueAsync();
                return;
            }

            try
            {
                IAPIResponse answer = await route.FetchAsync(new RouteFetchOptions { MaxRedirects = 0 });
                await Task.Delay(answerDelay);
                await route.FulfillAsync(new RouteFulfillOptions { Response = answer });
            }
            catch (PlaywrightException exception)
            {
                watch._routeFailures.Enqueue(exception.Message);
                await ContinueUnlessDroppedAsync(route);
            }
        });
        return watch;
    }

    private static async Task ContinueUnlessDroppedAsync(IRoute route)
    {
        try
        {
            await route.ContinueAsync();
        }
        catch (PlaywrightException)
        {
            // The browser already dropped this request for a newer one, so there is nothing to answer.
        }
    }
}
