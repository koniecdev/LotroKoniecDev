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

public sealed class PasswordResetRequestProcessorTests
{
    private readonly UserManager<ApplicationUser> _userManager = CreateUserManager();
    private readonly IPasswordResetEmailSender _emailSender =
        Substitute.For<IPasswordResetEmailSender>();

    [Fact]
    public async Task ProcessAsync_UserNoLongerExists_SucceedsWithoutSending()
    {
        Guid userId = Guid.NewGuid();
        _userManager.FindByIdAsync(userId.ToString()).Returns((ApplicationUser?)null);
        PasswordResetRequestProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new PasswordResetRequested(userId), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendPasswordResetEmailAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_DeletionIsScheduled_SucceedsWithoutSending()
    {
        // While a GDPR deletion is scheduled, the cancel link in the e-mail is the only way back, and
        // the processor is the one place that checks this (ADR-0038 decision 2).
        ApplicationUser user = CreateUser();
        user.DeletionScheduledAt = DateTimeOffset.UtcNow;
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        PasswordResetRequestProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new PasswordResetRequested(user.Id), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendPasswordResetEmailAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_UserHasNoEmailAddress_SucceedsWithoutSending()
    {
        ApplicationUser user = CreateUser();
        user.Email = null;
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        PasswordResetRequestProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new PasswordResetRequested(user.Id), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendPasswordResetEmailAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_ActiveUser_SendsTheEmailWithAFreshlyMintedToken()
    {
        ApplicationUser user = CreateUser();
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        _userManager.GeneratePasswordResetTokenAsync(user).Returns("fresh-token");
        _emailSender.SendPasswordResetEmailAsync(user.Id, user.Email!, "fresh-token", Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        PasswordResetRequestProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new PasswordResetRequested(user.Id), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.Received(1)
            .SendPasswordResetEmailAsync(user.Id, user.Email!, "fresh-token", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_SendingFails_PropagatesTheFailure()
    {
        ApplicationUser user = CreateUser();
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        _userManager.GeneratePasswordResetTokenAsync(user).Returns("fresh-token");
        Error smtpError = new("Email.SendFailed", "SMTP relay refused the message.");
        _emailSender.SendPasswordResetEmailAsync(user.Id, user.Email!, "fresh-token", Arg.Any<CancellationToken>())
            .Returns(Result.Failure(smtpError));
        PasswordResetRequestProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new PasswordResetRequested(user.Id), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(smtpError);
    }

    [Theory]
    [InlineData("this is not json at all")]
    [InlineData("{}")]
    [InlineData("""{"IdentityUserId":"not-a-guid"}""")]
    public void TryDeserialize_PoisonPayload_ReturnsTheNullVerdict(string poisonPayload)
    {
        PasswordResetRequestProcessor sut = CreateSut();

        object? message = sut.TryDeserialize(System.Text.Encoding.UTF8.GetBytes(poisonPayload));

        message.ShouldBeNull();
    }

    [Fact]
    public void TryDeserialize_ValidPayload_ReturnsTheTypedMessage()
    {
        Guid userId = Guid.CreateVersion7();
        PasswordResetRequestProcessor sut = CreateSut();

        object? message = sut.TryDeserialize(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new PasswordResetRequested(userId)));

        PasswordResetRequested typed = message.ShouldBeOfType<PasswordResetRequested>();
        typed.IdentityUserId.ShouldBe(userId);
    }

    private static ApplicationUser CreateUser() =>
        new()
        {
            Id = Guid.NewGuid(),
            UserName = "frodo",
            Email = "frodo@shire.me"
        };

    private PasswordResetRequestProcessor CreateSut() =>
        new(_userManager, _emailSender, NullLogger<PasswordResetRequestProcessor>.Instance);

    private static UserManager<ApplicationUser> CreateUserManager() =>
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);
}
