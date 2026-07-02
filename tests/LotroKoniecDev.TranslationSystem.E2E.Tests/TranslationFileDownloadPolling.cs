using LotroKoniecDev.TranslationSystem.E2E.Tests.Clients;

namespace LotroKoniecDev.TranslationSystem.E2E.Tests;

/// <summary>
/// Polls the anonymous translation-file download until the containerised tms-api's debounced
/// background rebuild (PERF-04, ADR-0021) has converged on the caller's condition. On timeout the
/// last snapshot is returned anyway, so the caller's regular assertions fail with the actually
/// observed response instead of a bare timeout exception.
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
