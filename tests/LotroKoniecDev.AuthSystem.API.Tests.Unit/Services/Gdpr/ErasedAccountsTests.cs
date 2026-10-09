using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Gdpr;

/// <summary>
/// The in-memory check must give the answer the SQL rule gives, and SQL compares byte for byte
/// (ADR-0065).
/// </summary>
public sealed class ErasedAccountsTests
{
    [Fact]
    public void Includes_AnAnonymizedAccountWithoutAPassword_ReturnsTrue()
    {
        ApplicationUser account = Account("anon-0123456789abcdef0123456789abcdef@anonymized.local", passwordHash: null);

        ErasedAccounts.Includes(account).ShouldBeTrue();
    }

    [Theory]
    [InlineData("anon-0123456789abcdef0123456789abcdef@anonymized.local", "a-password-hash")]
    [InlineData("frodo@shire.me", null)]
    [InlineData("0123456789abcdef0123456789abcdef@anonymized.local", null)]
    [InlineData("anon-0123@anonymized.local.example", null)]
    [InlineData("ANON-0123@ANONYMIZED.LOCAL", null)]
    [InlineData("an\u00ADon-0123@anonymized.local", null)]
    [InlineData("anon-0123@anonymized.lo\u00ADcal", null)]
    [InlineData(null, null)]
    public void Includes_AnythingElse_ReturnsFalse(string? email, string? passwordHash)
    {
        ApplicationUser account = Account(email, passwordHash);

        ErasedAccounts.Includes(account).ShouldBeFalse();
    }

    private static ApplicationUser Account(string? email, string? passwordHash) =>
        new()
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = passwordHash
        };
}
