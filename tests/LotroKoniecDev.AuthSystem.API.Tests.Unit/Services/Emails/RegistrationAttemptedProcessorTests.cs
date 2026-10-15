using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Monads;
using NSubstitute;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Emails;

public sealed class RegistrationAttemptedProcessorTests
{
    private const string AttemptedEmail = "Frodo@Shire.me";

    private readonly UserManager<ApplicationUser> _userManager = CreateUserManager();
    private readonly IAccountConfirmationEmailSender _emailSender =
        Substitute.For<IAccountConfirmationEmailSender>();

    public RegistrationAttemptedProcessorTests()
    {
        _userManager.NormalizeEmail(Arg.Any<string?>())
            .Returns(callInfo => callInfo.Arg<string?>()?.ToUpperInvariant());
    }

    [Fact]
    public async Task ProcessAsync_UserNoLongerExists_SucceedsWithoutSending()
    {
        Guid userId = Guid.NewGuid();
        _userManager.FindByIdAsync(userId.ToString()).Returns((ApplicationUser?)null);
        RegistrationAttemptedProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new RegistrationAttemptedOnExistingAccount(userId, AttemptedEmail), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendRegistrationAttemptedNoticeAsync(default, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_AccountWasErasedWhileTheMessageWaited_SucceedsWithoutSending()
    {
        ApplicationUser user = CreateUser();
        user.Email = $"{AnonymizationConstants.EmailPrefix}{Guid.NewGuid():N}{AnonymizationConstants.EmailDomain}";
        user.PasswordHash = null;
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        RegistrationAttemptedProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new RegistrationAttemptedOnExistingAccount(user.Id, AttemptedEmail), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendRegistrationAttemptedNoticeAsync(default, default!, default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProcessAsync_UserHasNoEmailAddress_SucceedsWithoutSending(string? email)
    {
        ApplicationUser user = CreateUser();
        user.Email = email;
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        RegistrationAttemptedProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new RegistrationAttemptedOnExistingAccount(user.Id, AttemptedEmail), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendRegistrationAttemptedNoticeAsync(default, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_AccountExists_SendsTheNoticeToTheAddressTheAccountHasNow()
    {
        ApplicationUser user = CreateUser();
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        _emailSender.SendRegistrationAttemptedNoticeAsync(user.Id, user.Email!, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        RegistrationAttemptedProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new RegistrationAttemptedOnExistingAccount(user.Id, AttemptedEmail), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.Received(1)
            .SendRegistrationAttemptedNoticeAsync(user.Id, user.Email!, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A notice that waited while the account moved to another address would tell the new address about an
    /// attempt on the old one (#860).
    /// </summary>
    [Fact]
    public async Task ProcessAsync_AccountMovedOffTheAddressThatWasTried_SucceedsWithoutSending()
    {
        ApplicationUser user = CreateUser();
        user.Email = "frodo@rivendell.example";
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        RegistrationAttemptedProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new RegistrationAttemptedOnExistingAccount(user.Id, AttemptedEmail), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendRegistrationAttemptedNoticeAsync(default, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_SendingFails_PropagatesTheFailure()
    {
        ApplicationUser user = CreateUser();
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        Error smtpError = new("Email.SendFailed", "SMTP relay refused the message.");
        _emailSender.SendRegistrationAttemptedNoticeAsync(user.Id, user.Email!, Arg.Any<CancellationToken>())
            .Returns(Result.Failure(smtpError));
        RegistrationAttemptedProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new RegistrationAttemptedOnExistingAccount(user.Id, AttemptedEmail), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(smtpError);
    }

    [Theory]
    [InlineData("this is not json at all")]
    [InlineData("{}")]
    [InlineData("""{"IdentityUserId":"not-a-guid"}""")]
    [InlineData("""{"IdentityUserId":"00000000-0000-0000-0000-000000000000","AttemptedEmail":"a@b.pl"}""")]
    [InlineData("""{"IdentityUserId":"0195f0f6-0000-7000-8000-000000000000"}""")]
    [InlineData("""{"IdentityUserId":"0195f0f6-0000-7000-8000-000000000000","AttemptedEmail":"  "}""")]
    public void TryDeserialize_PoisonPayload_ReturnsTheNullVerdict(string poisonPayload)
    {
        RegistrationAttemptedProcessor sut = CreateSut();

        object? message = sut.TryDeserialize(System.Text.Encoding.UTF8.GetBytes(poisonPayload));

        message.ShouldBeNull();
    }

    [Fact]
    public void TryDeserialize_ValidPayload_ReturnsTheTypedMessage()
    {
        Guid userId = Guid.CreateVersion7();
        RegistrationAttemptedProcessor sut = CreateSut();

        object? message = sut.TryDeserialize(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new RegistrationAttemptedOnExistingAccount(userId, AttemptedEmail)));

        RegistrationAttemptedOnExistingAccount typed = message.ShouldBeOfType<RegistrationAttemptedOnExistingAccount>();
        typed.IdentityUserId.ShouldBe(userId);
        typed.AttemptedEmail.ShouldBe(AttemptedEmail);
    }

    private static ApplicationUser CreateUser() =>
        new()
        {
            Id = Guid.NewGuid(),
            UserName = "frodo",
            Email = "frodo@shire.me",
            PasswordHash = "hash"
        };

    private RegistrationAttemptedProcessor CreateSut() =>
        new(_userManager, _emailSender, NullLogger<RegistrationAttemptedProcessor>.Instance);

    private static UserManager<ApplicationUser> CreateUserManager() =>
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);
}
