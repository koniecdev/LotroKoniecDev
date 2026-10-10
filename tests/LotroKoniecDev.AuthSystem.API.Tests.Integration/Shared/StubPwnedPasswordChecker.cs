using System.Collections.Concurrent;
using LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Stands in for Have I Been Pwned, so this suite never calls a third-party service and the many weak
/// test passwords it uses still pass. By default every password is clean. A test lists the passwords
/// that count as breached, or takes the service down; <see cref="Bases.AsyncLifetimeTestBase"/> resets
/// both before and after each test.
/// </summary>
#pragma warning disable CA1515
public sealed class StubPwnedPasswordChecker : IPwnedPasswordChecker
#pragma warning restore CA1515
{
    private readonly ConcurrentDictionary<string, byte> _breachedPasswords = new(StringComparer.Ordinal);
    private volatile bool _unavailable;

    public void MarkBreached(string password) => _breachedPasswords[password] = 0;

    public void TakeDown() => _unavailable = true;

    public void Reset()
    {
        _breachedPasswords.Clear();
        _unavailable = false;
    }

    public Task<PwnedPasswordVerdict> CheckAsync(string password, CancellationToken cancellationToken)
    {
        PwnedPasswordVerdict verdict = _unavailable
            ? PwnedPasswordVerdict.Unavailable
            : _breachedPasswords.ContainsKey(password)
                ? PwnedPasswordVerdict.Breached
                : PwnedPasswordVerdict.NotFound;

        return Task.FromResult(verdict);
    }
}
