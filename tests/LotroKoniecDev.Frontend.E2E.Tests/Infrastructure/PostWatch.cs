using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// Counts every POST. <see cref="HoldAnswersAsync"/> holds each answer for a moment, so a second click lands
/// while the first request is open on any machine (#871). A failed route is recorded, not thrown, and the
/// request is sent once more without the hold, so the test reports the record instead of a timeout.
/// </summary>
internal sealed class PostWatch
{
    private readonly ConcurrentQueue<string> _postedPaths = new();
    private readonly ConcurrentQueue<string> _routeFailures = new();

    private PostWatch()
    {
    }

    /// <summary>
    /// Expected without the fix, because the browser drops the first request on a second click, so check the
    /// POST count first.
    /// </summary>
    public IReadOnlyCollection<string> RouteFailures => _routeFailures;

    public int PostsTo(string path) => _postedPaths.Count(postedPath => postedPath == path);

    public static PostWatch StartCounting(IPage page)
    {
        PostWatch watch = new();
        page.Request += (_, request) =>
        {
            if (request.Method is "POST")
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
            if (route.Request.Method is not "POST")
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
