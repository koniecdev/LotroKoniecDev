using LotroKoniecDev.AuthSystem.API.Pages.Account;
using LotroKoniecDev.SharedKernel.Constants;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Pages.Account;

/// <summary>
/// The shape check the two e-mail-change pages run before they print a link's value or act on it. It
/// keeps arbitrary text off a branded page that carries a password-clearing button, and it is what
/// stops a malformed address reaching code that assumes one.
/// </summary>
public sealed class EmailLinkValueTests
{
    [Theory]
    [InlineData("frodo@shire.me")]
    [InlineData("frodo.baggins@shire.me")]
    [InlineData("frodo+bag@shire.me")]
    [InlineData("frodo_b@sub.shire.co.uk")]
    [InlineData("f@x.pl")]
    public void LooksLikeAnAddress_RealAddress_Accepts(string value)
    {
        EmailLinkValue.LooksLikeAnAddress(value).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    [InlineData("no@tld")]
    [InlineData("two@@at.pl")]
    [InlineData("spaces in@address.pl")]
    [InlineData("trailing@space.pl ")]
    [InlineData("<script>alert(1)</script>@x.pl")]
    [InlineData("Twoje konto zostało przejęte — kliknij tutaj")]
    public void LooksLikeAnAddress_AnythingElse_Refuses(string? value)
    {
        EmailLinkValue.LooksLikeAnAddress(value).ShouldBeFalse();
    }

    [Fact]
    public void LooksLikeAnAddress_AtTheLengthCeiling_Accepts()
    {
        string local = new('a', EmailConstants.MaxLength - "@shire.me".Length);
        string value = $"{local}@shire.me";

        value.Length.ShouldBe(EmailConstants.MaxLength);
        EmailLinkValue.LooksLikeAnAddress(value).ShouldBeTrue();
    }

    [Fact]
    public void LooksLikeAnAddress_OneCharacterOverTheCeiling_Refuses()
    {
        // The length check runs before the regex on purpose, so an over-long value never reaches it.
        string local = new('a', EmailConstants.MaxLength - "@shire.me".Length + 1);
        string value = $"{local}@shire.me";

        value.Length.ShouldBe(EmailConstants.MaxLength + 1);
        EmailLinkValue.LooksLikeAnAddress(value).ShouldBeFalse();
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("a-a-a-a-a-a-a-a-a-a-a-a-a-a-a-a-a-a-a-a-a-a-a-a-a-")]
    [InlineData("a.a.a.a.a.a.a.a.a.a.a.a.a.a.a.a.a.a.a.a.a.a.a.a.a.")]
    public void LooksLikeAnAddress_HostileInputThatNeverMatches_ReturnsQuicklyInsteadOfHanging(string seed)
    {
        // The classic backtracking bait for an address pattern: a long run that almost matches and then
        // fails. It has to answer, not spin.
        string value = string.Concat(Enumerable.Repeat(seed, 5))[..EmailConstants.MaxLength];

        EmailLinkValue.LooksLikeAnAddress(value).ShouldBeFalse();
    }
}
