using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using NSubstitute;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Emails;

public sealed class EmailChangeRequestProcessorTests
{
    private const string CurrentEmail = "frodo@shire.me";
    private const string NewEmail = "frodo@rivendell.example";

    private readonly UserManager<ApplicationUser> _userManager = CreateUserManager();
    private readonly IEmailChangeEmailSender _emailSender = Substitute.For<IEmailChangeEmailSender>();

    public EmailChangeRequestProcessorTests()
    {
        _userManager.NormalizeEmail(Arg.Any<string?>())
            .Returns(callInfo => callInfo.Arg<string?>()?.ToUpperInvariant());
    }

    [Fact]
    public async Task ProcessAsync_UserNoLongerExists_SucceedsWithoutSending()
    {
        Guid userId = Guid.NewGuid();
        _userManager.FindByIdAsync(userId.ToString()).Returns((ApplicationUser?)null);
        EmailChangeRequestProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new EmailChangeRequested(userId, CurrentEmail, NewEmail), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs().SendVerificationAsync(default, default!, default!, default);
        await _emailSender.DidNotReceiveWithAnyArgs().SendChangeRequestedWarningAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_AddressAlreadyMovedOn_SendsNothing()
    {
        // A late or replayed delivery. Without this check the warning would be addressed to
        // user.Email, which by now is the address the request was aiming at — so the "somebody wants
        // to move your account" e-mail would land in the mailbox that asked for the move.
        ApplicationUser user = CreateUser();
        user.Email = NewEmail;
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        EmailChangeRequestProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new EmailChangeRequested(user.Id, CurrentEmail, NewEmail), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs().SendVerificationAsync(default, default!, default!, default);
        await _emailSender.DidNotReceiveWithAnyArgs().SendChangeRequestedWarningAsync(default, default!, default!, default);
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
        EmailChangeRequestProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new EmailChangeRequested(user.Id, CurrentEmail, NewEmail), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs().SendVerificationAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_OpenRequest_SendsTheLinkToTheNewAddressAndTheWarningToTheOldOne()
    {
        ApplicationUser user = CreateUser();
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        _userManager.GenerateUserTokenAsync(
                user,
                EmailChangeTokenProvider.ProviderName,
                EmailChangeTokenProvider.PurposeFor(NewEmail))
            .Returns("fresh-change-token");
        _emailSender.SendVerificationAsync(user.Id, NewEmail, "fresh-change-token", Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _emailSender.SendChangeRequestedWarningAsync(user.Id, CurrentEmail, NewEmail, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        EmailChangeRequestProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new EmailChangeRequested(user.Id, CurrentEmail, NewEmail), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.Received(1)
            .SendVerificationAsync(user.Id, NewEmail, "fresh-change-token", Arg.Any<CancellationToken>());
        await _emailSender.Received(1)
            .SendChangeRequestedWarningAsync(user.Id, CurrentEmail, NewEmail, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_CurrentAddressDiffersOnlyByCase_StillTreatsTheRequestAsOpen()
    {
        ApplicationUser user = CreateUser();
        user.Email = "Frodo@Shire.ME";
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        _emailSender.SendVerificationAsync(user.Id, NewEmail, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _emailSender.SendChangeRequestedWarningAsync(
                user.Id, CurrentEmail, NewEmail, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        EmailChangeRequestProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new EmailChangeRequested(user.Id, CurrentEmail, NewEmail), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.Received(1)
            .SendVerificationAsync(user.Id, NewEmail, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_VerificationSendFails_PropagatesTheFailureAndSkipsTheWarning()
    {
        ApplicationUser user = CreateUser();
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        Error smtpError = new("Email.SendFailed", "SMTP relay refused the message.");
        _emailSender.SendVerificationAsync(user.Id, NewEmail, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(smtpError));
        EmailChangeRequestProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new EmailChangeRequested(user.Id, CurrentEmail, NewEmail), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(smtpError);
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendChangeRequestedWarningAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_WarningSendFails_AsksForARetry()
    {
        // The warning is the message this flow cannot afford to lose, so a failure on it has to keep
        // the message on the queue even though the verification link already went out.
        ApplicationUser user = CreateUser();
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        Error smtpError = new("Email.SendFailed", "SMTP relay refused the message.");
        _emailSender.SendVerificationAsync(user.Id, NewEmail, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _emailSender.SendChangeRequestedWarningAsync(
                user.Id, CurrentEmail, NewEmail, Arg.Any<CancellationToken>())
            .Returns(Result.Failure(smtpError));
        EmailChangeRequestProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new EmailChangeRequested(user.Id, CurrentEmail, NewEmail), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(smtpError);
    }

    [Theory]
    [InlineData("this is not json at all")]
    [InlineData("{}")]
    [InlineData("""{"IdentityUserId":"not-a-guid"}""")]
    [InlineData("""{"IdentityUserId":"0195f0f6-0000-7000-8000-000000000000","CurrentEmail":"","NewEmail":"a@b.pl"}""")]
    [InlineData("""{"IdentityUserId":"0195f0f6-0000-7000-8000-000000000000","CurrentEmail":"a@b.pl","NewEmail":"  "}""")]
    public void TryDeserialize_PoisonPayload_ReturnsTheNullVerdict(string poisonPayload)
    {
        EmailChangeRequestProcessor sut = CreateSut();

        object? message = sut.TryDeserialize(System.Text.Encoding.UTF8.GetBytes(poisonPayload));

        message.ShouldBeNull();
    }

    [Fact]
    public void TryDeserialize_ValidPayload_ReturnsTheTypedMessage()
    {
        Guid userId = Guid.CreateVersion7();
        EmailChangeRequestProcessor sut = CreateSut();

        object? message = sut.TryDeserialize(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new EmailChangeRequested(userId, CurrentEmail, NewEmail)));

        EmailChangeRequested typed = message.ShouldBeOfType<EmailChangeRequested>();
        typed.IdentityUserId.ShouldBe(userId);
        typed.CurrentEmail.ShouldBe(CurrentEmail);
        typed.NewEmail.ShouldBe(NewEmail);
    }

    private static ApplicationUser CreateUser() =>
        new()
        {
            Id = Guid.NewGuid(),
            UserName = "frodo",
            Email = CurrentEmail
        };

    private EmailChangeRequestProcessor CreateSut() =>
        new(_userManager, _emailSender, NullLogger<EmailChangeRequestProcessor>.Instance);

    private static UserManager<ApplicationUser> CreateUserManager() =>
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);
}
