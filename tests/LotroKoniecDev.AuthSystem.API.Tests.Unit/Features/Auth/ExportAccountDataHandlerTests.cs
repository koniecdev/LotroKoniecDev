using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;
using NSubstitute;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Features.Auth;

/// <summary>
/// The audit line is the only trace an export leaves, and the return value does not show it. So these
/// tests read the log: every attempt must say who, from where and with what, and never print the full
/// address (#690).
/// </summary>
public sealed class ExportAccountDataHandlerTests
{
    private const string Email = "frodo@shire.me";
    private const string MaskedEmail = "f***@shire.me";
    private const string Password = "TestPass1!";
    private const string IpAddress = "203.0.113.7";
    private const string UserAgent = "Mozilla/5.0 (test)";

    private readonly UserManager<ApplicationUser> _userManager =
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);

    private readonly CapturingLogger _logger = new();

    [Fact]
    public async Task Handle_CorrectPassword_ReturnsTheExportAndLogsWhoTookItAndFromWhere()
    {
        ApplicationUser user = StubUser(passwordValid: true);
        ExportAccountData.Handler sut = new(_userManager, _logger);

        Result<AccountDataExportResponse> result = await sut.Handle(
            new ExportAccountData.Query(user.Id.ToString(), Password, IpAddress, UserAgent), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.AuthData.Email.ShouldBe(Email);

        CapturingLogger.Entry entry = _logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldContain(user.Id.ToString());
        entry.Message.ShouldContain(MaskedEmail);
        entry.Message.ShouldContain(IpAddress);
        entry.Message.ShouldContain(UserAgent);
        entry.Message.ShouldNotContain(Email);
        entry.Message.ShouldNotContain(Password);
    }

    [Fact]
    public async Task Handle_WrongPassword_NeverWritesTheTypedPasswordToTheLog()
    {
        // A mistyped password is often one character away from the real one.
        const string typedPassword = "WrongPass1!";
        ApplicationUser user = StubUser(passwordValid: false);
        ExportAccountData.Handler sut = new(_userManager, _logger);

        await sut.Handle(
            new ExportAccountData.Query(user.Id.ToString(), typedPassword, IpAddress, UserAgent), CancellationToken.None);

        _logger.Entries.ShouldHaveSingleItem().Message.ShouldNotContain(typedPassword);
    }

    [Fact]
    public async Task Handle_AccountWithoutAnAddress_StillLogsTheAttempt()
    {
        ApplicationUser user = StubUser(passwordValid: true);
        user.Email = null;
        ExportAccountData.Handler sut = new(_userManager, _logger);

        Result<AccountDataExportResponse> result = await sut.Handle(
            new ExportAccountData.Query(user.Id.ToString(), Password, IpAddress, UserAgent), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        CapturingLogger.Entry entry = _logger.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldContain("(***)");
        entry.Message.ShouldContain(IpAddress);
    }

    [Theory]
    [InlineData("WrongPass1!")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_WrongOrMissingPassword_RefusesAndLogsTheAttempt(string password)
    {
        ApplicationUser user = StubUser(passwordValid: false);
        ExportAccountData.Handler sut = new(_userManager, _logger);

        Result<AccountDataExportResponse> result = await sut.Handle(
            new ExportAccountData.Query(user.Id.ToString(), password, IpAddress, UserAgent), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();

        CapturingLogger.Entry entry = _logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain(user.Id.ToString());
        entry.Message.ShouldContain(MaskedEmail);
        entry.Message.ShouldContain(IpAddress);
        entry.Message.ShouldContain(UserAgent);
        entry.Message.ShouldNotContain(Email);
    }

    [Fact]
    public async Task Handle_UnknownUser_RefusesAndLogsTheAttempt()
    {
        string userId = Guid.NewGuid().ToString();
        _userManager.FindByIdAsync(userId).Returns((ApplicationUser?)null);
        ExportAccountData.Handler sut = new(_userManager, _logger);

        Result<AccountDataExportResponse> result = await sut.Handle(
            new ExportAccountData.Query(userId, Password, IpAddress, UserAgent), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Auth.UserNotFound");

        CapturingLogger.Entry entry = _logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain(userId);
        entry.Message.ShouldContain(IpAddress);
        entry.Message.ShouldContain(UserAgent);
    }

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

    private sealed class CapturingLogger : ILogger<ExportAccountData.Handler>
    {
        private readonly List<Entry> _entries = [];

        public IReadOnlyList<Entry> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _entries.Add(new Entry(logLevel, formatter(state, exception)));
        }

        internal sealed record Entry(LogLevel Level, string Message);
    }
}
