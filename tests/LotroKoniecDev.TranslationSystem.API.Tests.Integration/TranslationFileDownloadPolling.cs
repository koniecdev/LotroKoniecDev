namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration;

/// <summary>
/// Waits for the debounced rebuild (ADR-0021). On timeout it returns the last snapshot, so the caller's
/// assertions fail on the response actually seen, not on a bare timeout.
/// </summary>
internal static class TranslationFileDownloadPolling
{
    private static readonly TimeSpan ConvergenceTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    public static async Task<(HttpResponseMessage Response, string Content)> DownloadWhenConvergedAsync(
        HttpClient client,
        string route,
        Func<HttpResponseMessage, string, bool> hasConverged)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + ConvergenceTimeout;

        while (true)
        {
            HttpResponseMessage response = await client.GetAsync(route);
            string content = await response.Content.ReadAsStringAsync();

            if (hasConverged(response, content) || DateTimeOffset.UtcNow >= deadline)
            {
                return (response, content);
            }

            response.Dispose();
            await Task.Delay(PollInterval);
        }
    }
}
