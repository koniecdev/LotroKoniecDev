using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using NSubstitute;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Emails;

public sealed class EmailConfirmationRequestProcessorTests
{
    private readonly UserManager<ApplicationUser> _userManager = CreateUserManager();
    private readonly IAccountConfirmationEmailSender _emailSender =
        Substitute.For<IAccountConfirmationEmailSender>();

    [Fact]
    public async Task ProcessAsync_UserNoLongerExists_SucceedsWithoutSending()
    {
        Guid userId = Guid.NewGuid();
        _userManager.FindByIdAsync(userId.ToString()).Returns((ApplicationUser?)null);
        EmailConfirmationRequestProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new EmailConfirmationRequested(userId), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendEmailConfirmationAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_UserAlreadyConfirmed_SucceedsWithoutSending()
    {
        ApplicationUser user = CreateUser(emailConfirmed: true);
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        EmailConfirmationRequestProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new EmailConfirmationRequested(user.Id), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendEmailConfirmationAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_UserHasNoEmailAddress_SucceedsWithoutSending()
    {
        ApplicationUser user = CreateUser(emailConfirmed: false);
        user.Email = null;
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        EmailConfirmationRequestProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new EmailConfirmationRequested(user.Id), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendEmailConfirmationAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_UnconfirmedUser_SendsTheEmailWithAFreshlyMintedToken()
    {
        ApplicationUser user = CreateUser(emailConfirmed: false);
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        _userManager.GenerateEmailConfirmationTokenAsync(user).Returns("fresh-token");
        _emailSender.SendEmailConfirmationAsync(user.Id, user.Email!, "fresh-token", Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        EmailConfirmationRequestProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new EmailConfirmationRequested(user.Id), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.Received(1)
            .SendEmailConfirmationAsync(user.Id, user.Email!, "fresh-token", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_SendingFails_PropagatesTheFailure()
    {
        ApplicationUser user = CreateUser(emailConfirmed: false);
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        _userManager.GenerateEmailConfirmationTokenAsync(user).Returns("fresh-token");
        Error smtpError = new("Email.SendFailed", "SMTP relay refused the message.");
        _emailSender.SendEmailConfirmationAsync(user.Id, user.Email!, "fresh-token", Arg.Any<CancellationToken>())
            .Returns(Result.Failure(smtpError));
        EmailConfirmationRequestProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new EmailConfirmationRequested(user.Id), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(smtpError);
    }

    private static ApplicationUser CreateUser(bool emailConfirmed) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserName = "frodo",
            Email = "frodo@shire.me",
            EmailConfirmed = emailConfirmed
        };

    private EmailConfirmationRequestProcessor CreateSut() =>
        new(_userManager, _emailSender, NullLogger<EmailConfirmationRequestProcessor>.Instance);

    private static UserManager<ApplicationUser> CreateUserManager() =>
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);
}
