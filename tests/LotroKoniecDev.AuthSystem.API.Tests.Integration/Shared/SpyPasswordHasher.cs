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

    public int VerifyCount => Volatile.Read(ref _verifyCount);

    public string HashPassword(ApplicationUser user, string password) =>
        _inner.HashPassword(user, password);

    public PasswordVerificationResult VerifyHashedPassword(
        ApplicationUser user,
        string hashedPassword,
        string providedPassword)
    {
        Interlocked.Increment(ref _verifyCount);
        return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _verifyCount, 0);
    }
}
