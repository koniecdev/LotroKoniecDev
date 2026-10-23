using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// A request only commits the outbox row (ADR-0038), so the publish, the delivery and the inbox record are
/// waited for, never assumed.
/// </summary>
internal static class OutboxAssertions
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Returns the latest read, or null; the assertions stay in the test.
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
    /// The inbox row is how we know the consumer finished. Returns the row count, or 0 when the time runs out.
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

    /// <summary>
    /// The request writes its row in its own transaction, so unlike the mail it needs no wait.
    /// </summary>
    public static async Task<int> CountOutboxRowsAsync(AuthSystemApiFactory factory, string type)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.OutboxMessages.AsNoTracking().CountAsync(row => row.Type == type);
    }

    public static async Task<int> CountInboxRowsAsync(AuthSystemApiFactory factory, Guid messageId)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.InboxMessages.AsNoTracking().CountAsync(row => row.MessageId == messageId);
    }
}
