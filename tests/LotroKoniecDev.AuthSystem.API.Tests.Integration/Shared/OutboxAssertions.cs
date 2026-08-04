using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Polling helpers over the outbox and inbox tables for tests that assert on the asynchronous
/// tail of the e-mail pipeline: a request only commits the outbox row (ADR-0038), so everything
/// after it — publish, delivery, the inbox record — has to be awaited as state, never assumed.
/// </summary>
internal static class OutboxAssertions
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Polls the outbox until a row matches or the timeout passes, then returns the latest
    /// snapshot (or null) — the assertions on it stay in the test body.
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
    /// Waits until the inbox records the message as processed (the "consumer finished" marker)
    /// and returns the row count — 0 when the timeout passes first.
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
