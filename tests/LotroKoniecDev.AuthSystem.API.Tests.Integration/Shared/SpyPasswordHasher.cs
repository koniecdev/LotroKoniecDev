using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Counts password verifications. The login page shows the same text for every failure, so the hashing
/// cost is the only thing that could still tell the failures apart, and a test has to see it to pin it.
/// </summary>
#pragma warning disable CA1515
public sealed class SpyPasswordHasher : IPasswordHasher<ApplicationUser>
#pragma warning restore CA1515
{
    private readonly PasswordHasher<ApplicationUser> _inner = new();
    private int _verifyCount;
    private int _dummyVerifyCount;

    public int VerifyCount => Volatile.Read(ref _verifyCount);

    /// <summary>
    /// The verifications made for a user with no id. The login page checks its dummy hash that way, so this
    /// tells the dummy hash apart from a real account's hash.
    /// </summary>
    public int DummyVerifyCount => Volatile.Read(ref _dummyVerifyCount);

    public string HashPassword(ApplicationUser user, string password) =>
        _inner.HashPassword(user, password);

    public PasswordVerificationResult VerifyHashedPassword(
        ApplicationUser user,
        string hashedPassword,
        string providedPassword)
    {
        Interlocked.Increment(ref _verifyCount);
        if (user.Id == Guid.Empty)
        {
            Interlocked.Increment(ref _dummyVerifyCount);
        }

        return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _verifyCount, 0);
        Interlocked.Exchange(ref _dummyVerifyCount, 0);
    }
}
