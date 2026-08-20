using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Helpers that poll the outbox and inbox tables, for tests that check the part of the e-mail pipeline
/// that runs later. A request only commits the outbox row (ADR-0038), so everything after it, the
/// publish, the delivery and the inbox record, has to be waited for and never assumed.
/// </summary>
internal static class OutboxAssertions
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Polls the outbox until a row matches or the time runs out, then returns what it last read, or
    /// null. The assertions on it stay in the test itself.
    /// </summary>
    public static async Task<OutboxMessage?> WaitForOutboxRowAsync(
        AuthSystemApiFactory factory,
        Func<OutboxMessage, bool> predicate,
        TimeSpan? timeout = null)
    {
        using CancellationTokenSource waitWindow = new(timeout ?? DefaultTimeout);

        while (true)
        {
            await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

            List<OutboxMessage> rows = await db.OutboxMessages.AsNoTracking().ToListAsync();
            OutboxMessage? match = rows.FirstOrDefault(predicate);

            if (match is not null || waitWindow.IsCancellationRequested)
            {
                return match;
            }

            await Task.Delay(100);
        }
    }

    /// <summary>
    /// Waits until the inbox records the message as processed, which is how we know the consumer
    /// finished, and returns the number of rows. It returns 0 when the time runs out first.
    /// </summary>
    public static async Task<int> WaitForInboxRowsAsync(
        AuthSystemApiFactory factory,
        Guid messageId,
        TimeSpan? timeout = null)
    {
        using CancellationTokenSource waitWindow = new(timeout ?? DefaultTimeout);

        while (true)
        {
            int count = await CountInboxRowsAsync(factory, messageId);
            if (count > 0 || waitWindow.IsCancellationRequested)
            {
                return count;
            }

            await Task.Delay(100);
        }
    }

    public static async Task<int> CountInboxRowsAsync(AuthSystemApiFactory factory, Guid messageId)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.InboxMessages.AsNoTracking().CountAsync(row => row.MessageId == messageId);
    }
}
