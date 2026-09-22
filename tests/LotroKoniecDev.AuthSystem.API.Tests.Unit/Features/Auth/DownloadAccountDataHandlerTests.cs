using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.AuthSystem.API.Tests.Unit.Shared;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;
using NSubstitute;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Features.Auth;

/// <summary>
/// "Every export attempt is in the audit log" is an acceptance criterion of #690, and a log line does
/// not show in the return value. So the lines themselves are pinned here: one per outcome, naming the
/// account and the client, and never the raw address.
/// </summary>
public sealed class DownloadAccountDataHandlerTests
{
    private const string Email = "frodo@shire.me";
    private const string Password = "Correct-Horse-1!";
    private const string IpAddress = "203.0.113.7";
    private const string UserAgent = "Mozilla/5.0 (QA)";

    private readonly UserManager<ApplicationUser> _userManager = CreateUserManager();
    private readonly CapturingLogger<DownloadAccountData.Handler> _logger = new();

    [Fact]
    public async Task Handle_CorrectPassword_LogsTheHandoverWithTheAccountAndTheClient()
    {
        ApplicationUser user = StubUser(passwordValid: true);

        Result<AccountDataExportResponse> result = await CreateSut().Handle(QueryFor(user, Password), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        CapturingLogger<DownloadAccountData.Handler>.LogEntry entry = _logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.EventId.ShouldBe(EventIds.ExportDataDownloaded);
        entry.Message.ShouldContain(user.Id.ToString());
        entry.Message.ShouldContain(IpAddress);
        entry.Message.ShouldContain(UserAgent);
    }

    [Fact]
    public async Task Handle_WrongPassword_LogsTheRefusalWithItsReason()
    {
        ApplicationUser user = StubUser(passwordValid: false);

        Result<AccountDataExportResponse> result = await CreateSut().Handle(QueryFor(user, "wrong"), CancellationToken.None);

        result.Error.Code.ShouldBe("Auth.InvalidCurrentPassword");
        CapturingLogger<DownloadAccountData.Handler>.LogEntry entry = _logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.EventId.ShouldBe(EventIds.ExportDataRefused);
        entry.Message.ShouldContain(user.Id.ToString());
        entry.Message.ShouldContain("the password did not match");
        entry.Message.ShouldContain(IpAddress);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_NoPassword_LogsTheRefusalWithItsReason(string password)
    {
        ApplicationUser user = StubUser(passwordValid: true);

        Result<AccountDataExportResponse> result = await CreateSut().Handle(QueryFor(user, password), CancellationToken.None);

        result.Error.Code.ShouldBe("Auth.ExportPasswordRequired");
        CapturingLogger<DownloadAccountData.Handler>.LogEntry entry = _logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.EventId.ShouldBe(EventIds.ExportDataRefused);
        entry.Message.ShouldContain(user.Id.ToString());
        entry.Message.ShouldContain("no password was sent");
    }

    [Fact]
    public async Task Handle_TokenNamesAnAccountThatIsGone_LogsTheRefusalAgainstTheIdTheTokenCarried()
    {
        // Reachable: an access token outlives the erasure of its account by a few minutes (ADR-0049).
        string userId = Guid.NewGuid().ToString();
        _userManager.FindByIdAsync(userId).Returns((ApplicationUser?)null);

        Result<AccountDataExportResponse> result = await CreateSut().Handle(
            new DownloadAccountData.Query(userId, Password, IpAddress, UserAgent), CancellationToken.None);

        result.Error.Code.ShouldBe("Auth.UserNotFound");
        CapturingLogger<DownloadAccountData.Handler>.LogEntry entry = _logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.EventId.ShouldBe(EventIds.ExportDataRefusedForUnknownAccount);
        entry.Message.ShouldContain(userId);
        entry.Message.ShouldContain(IpAddress);
    }

    [Theory]
    [InlineData(true, Password)]
    [InlineData(false, "wrong")]
    [InlineData(true, "")]
    public async Task Handle_AnyOutcome_NeverWritesTheRawAddress(bool passwordValid, string password)
    {
        ApplicationUser user = StubUser(passwordValid);

        await CreateSut().Handle(QueryFor(user, password), CancellationToken.None);

        _logger.Entries.ShouldHaveSingleItem().Message.ShouldNotContain(Email);
    }

    private DownloadAccountData.Handler CreateSut() => new(_userManager, _logger);

    private static DownloadAccountData.Query QueryFor(ApplicationUser user, string password) =>
        new(user.Id.ToString(), password, IpAddress, UserAgent);

    private ApplicationUser StubUser(bool passwordValid)
    {
        ApplicationUser user = new()
        {
            Id = Guid.NewGuid(),
            UserName = "frodo",
            Email = Email
        };

        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        _userManager.CheckPasswordAsync(user, Arg.Any<string>()).Returns(passwordValid);
        _userManager.GetRolesAsync(user).Returns(["Translator"]);

        return user;
    }

    private static UserManager<ApplicationUser> CreateUserManager() =>
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);
}
