using LotroKoniecDev.TranslationSystem.E2E.Tests.Clients;

namespace LotroKoniecDev.TranslationSystem.E2E.Tests;

/// <summary>
/// Waits for the debounced rebuild (ADR-0021). On timeout it returns the last snapshot, so the caller's
/// assertions fail on the response actually seen, not on a bare timeout.
/// </summary>
internal static class TranslationFileDownloadPolling
{
    private static readonly TimeSpan ConvergenceTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public static async Task<(HttpResponseMessage Response, string Content)> DownloadWhenConvergedAsync(
        TranslationSystemApiClient client,
        string language,
        Func<HttpResponseMessage, string, bool> hasConverged)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + ConvergenceTimeout;

        while (true)
        {
            HttpResponseMessage response = await client.DownloadFileRawAsync(language);
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
