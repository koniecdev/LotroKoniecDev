namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// The wait every mail spy uses. A request only commits an outbox row (commit -> relay -> delivery ->
/// spy, ADR-0038), so a test waits for its mail. A wait that runs out throws: a missing mail fails the
/// test where it was expected, instead of letting the test go on with nothing and pass (#950).
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
