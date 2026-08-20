using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using NSubstitute;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Identity;

/// <summary>
/// The revert token is the only thing standing between a stolen password and a permanently lost
/// account (ADR-0048), so its rules are pinned here rather than left to the page that uses it.
/// </summary>
public sealed class EmailChangeRevertTokenProviderTests
{
    private const string PreviousEmail = "frodo@shire.me";
    private const string NewEmail = "attacker@mordor.example";

    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Purpose = EmailChangeRevertTokenProvider.PurposeFor(PreviousEmail, NewEmail);

    private readonly IDataProtectionProvider _dataProtectionProvider = new EphemeralDataProtectionProvider();
    private readonly UserManager<ApplicationUser> _userManager = CreateUserManager();

    [Fact]
    public async Task ValidateAsync_TokenItJustIssued_Accepts()
    {
        ApplicationUser user = CreateUser();
        EmailChangeRevertTokenProvider sut = CreateSut(Now);

        string token = await sut.GenerateAsync(Purpose, _userManager, user);
        bool valid = await sut.ValidateAsync(Purpose, token, _userManager, user);

        valid.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateAsync_SecurityStampChangedAfterIssuing_StillAccepts()
    {
        // This assertion IS ADR-0048. Changing the password rotates the security stamp, and that is the
        // first thing whoever took the account over will do. A stamp-bound token, which is what every
        // other token in this system uses, would be dead here — and the owner would have no way back.
        ApplicationUser user = CreateUser();
        EmailChangeRevertTokenProvider sut = CreateSut(Now);
        string token = await sut.GenerateAsync(Purpose, _userManager, user);

        user.SecurityStamp = Guid.NewGuid().ToString();
        bool valid = await sut.ValidateAsync(Purpose, token, _userManager, user);

        valid.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateAsync_TokenOlderThanTheWindow_Refuses()
    {
        ApplicationUser user = CreateUser();
        EmailChangeRevertTokenProvider issuer = CreateSut(Now);
        string token = await issuer.GenerateAsync(Purpose, _userManager, user);

        EmailChangeRevertTokenProvider later = CreateSut(Now + TimeSpan.FromDays(14) + TimeSpan.FromMinutes(1));
        bool valid = await later.ValidateAsync(Purpose, token, _userManager, user);

        valid.ShouldBeFalse();
    }

    [Fact]
    public async Task ValidateAsync_OneMinuteBeforeExpiry_Accepts()
    {
        ApplicationUser user = CreateUser();
        EmailChangeRevertTokenProvider issuer = CreateSut(Now);
        string token = await issuer.GenerateAsync(Purpose, _userManager, user);

        EmailChangeRevertTokenProvider later = CreateSut(Now + TimeSpan.FromDays(14) - TimeSpan.FromMinutes(1));
        bool valid = await later.ValidateAsync(Purpose, token, _userManager, user);

        valid.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateAsync_PurposeNamesADifferentTargetAddress_Refuses()
    {
        // The addresses travel in the link's query string, so editing either of them has to fail.
        ApplicationUser user = CreateUser();
        EmailChangeRevertTokenProvider sut = CreateSut(Now);
        string token = await sut.GenerateAsync(Purpose, _userManager, user);

        bool valid = await sut.ValidateAsync(
            EmailChangeRevertTokenProvider.PurposeFor(PreviousEmail, "somebody-else@example.com"),
            token,
            _userManager,
            user);

        valid.ShouldBeFalse();
    }

    [Fact]
    public async Task ValidateAsync_PurposeNamesADifferentPreviousAddress_Refuses()
    {
        ApplicationUser user = CreateUser();
        EmailChangeRevertTokenProvider sut = CreateSut(Now);
        string token = await sut.GenerateAsync(Purpose, _userManager, user);

        bool valid = await sut.ValidateAsync(
            EmailChangeRevertTokenProvider.PurposeFor("someone@example.com", NewEmail),
            token,
            _userManager,
            user);

        valid.ShouldBeFalse();
    }

    [Fact]
    public async Task ValidateAsync_TokenIssuedForAnotherUser_Refuses()
    {
        EmailChangeRevertTokenProvider sut = CreateSut(Now);
        string token = await sut.GenerateAsync(Purpose, _userManager, CreateUser());

        bool valid = await sut.ValidateAsync(Purpose, token, _userManager, CreateUser());

        valid.ShouldBeFalse();
    }

    [Fact]
    public async Task ValidateAsync_TokenProtectedByAnotherKeyRing_Refuses()
    {
        // What a lost data-protection keyring looks like from here. It has to read as "no token", not
        // as a crash on a page a person reached from their inbox.
        ApplicationUser user = CreateUser();
        string token = await CreateSut(Now).GenerateAsync(Purpose, _userManager, user);

        EmailChangeRevertTokenProvider otherDeployment = new(
            new EphemeralDataProtectionProvider(),
            CreateTimeProvider(Now),
            Microsoft.Extensions.Options.Options.Create(new EmailChangeRevertTokenProviderOptions()));

        bool valid = await otherDeployment.ValidateAsync(Purpose, token, _userManager, user);

        valid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64-at-all!!")]
    [InlineData("Zm9vYmFy")]
    public async Task ValidateAsync_MalformedToken_Refuses(string token)
    {
        EmailChangeRevertTokenProvider sut = CreateSut(Now);

        bool valid = await sut.ValidateAsync(Purpose, token, _userManager, CreateUser());

        valid.ShouldBeFalse();
    }

    [Fact]
    public async Task CanGenerateTwoFactorTokenAsync_Always_SaysNo()
    {
        // It backs an e-mailed link and nothing else. Offering it as a login step would turn a
        // 14-day recovery token into a second factor.
        EmailChangeRevertTokenProvider sut = CreateSut(Now);

        bool canGenerate = await sut.CanGenerateTwoFactorTokenAsync(_userManager, CreateUser());

        canGenerate.ShouldBeFalse();
    }

    private EmailChangeRevertTokenProvider CreateSut(DateTimeOffset now) =>
        new(
            _dataProtectionProvider,
            CreateTimeProvider(now),
            Microsoft.Extensions.Options.Options.Create(new EmailChangeRevertTokenProviderOptions()));

    private static ApplicationUser CreateUser() =>
        new()
        {
            Id = Guid.NewGuid(),
            UserName = "frodo",
            Email = NewEmail,
            SecurityStamp = Guid.NewGuid().ToString()
        };

    private static TimeProvider CreateTimeProvider(DateTimeOffset now)
    {
        TimeProvider timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(now);
        return timeProvider;
    }

    private static UserManager<ApplicationUser> CreateUserManager() =>
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);
}
