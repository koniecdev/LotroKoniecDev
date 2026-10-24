namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// A request only commits an outbox row (ADR-0038), so a test waits for its mail. A wait that runs out
/// throws, so a missing mail fails where it was expected (#950).
/// </summary>
internal static class MailWait
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public static async Task UntilAsync(Func<bool> arrived, string expectedMail, TimeSpan? timeout)
    {
        TimeSpan window = timeout ?? DefaultTimeout;
        using CancellationTokenSource waitWindow = new(window);

        while (true)
        {
            // Read the clock before the spy, so a mail that lands in the last moment still counts.
            bool timedOut = waitWindow.IsCancellationRequested;
            if (arrived())
            {
                return;
            }

            if (timedOut)
            {
                throw new TimeoutException($"{expectedMail} did not arrive within {window.TotalSeconds:0.#} s.");
            }

            await Task.Delay(50);
        }
    }
}
