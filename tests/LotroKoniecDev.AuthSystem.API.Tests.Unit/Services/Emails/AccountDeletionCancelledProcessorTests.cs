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

public sealed class AccountDeletionCancelledProcessorTests
{
    private readonly UserManager<ApplicationUser> _userManager = CreateUserManager();
    private readonly IAccountDeletionEmailSender _emailSender =
        Substitute.For<IAccountDeletionEmailSender>();

    [Fact]
    public async Task ProcessAsync_UserNoLongerExists_SucceedsWithoutSending()
    {
        Guid userId = Guid.NewGuid();
        _userManager.FindByIdAsync(userId.ToString()).Returns((ApplicationUser?)null);
        AccountDeletionCancelledProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new AccountDeletionCancelled(userId), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendDeletionCancelledEmailAsync(default, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_DeletionIsScheduledAgain_SucceedsWithoutSending()
    {
        // The mirror drift guard (ADR-0038 decision 2): if deletion was re-scheduled in the gap,
        // "your account was kept" is a lie — and post-finalization DLQ replays land here too,
        // because erasure keeps DeletionScheduledAt set as its audit trace.
        ApplicationUser user = CreateUser();
        user.DeletionScheduledAt = DateTimeOffset.UtcNow;
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        AccountDeletionCancelledProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new AccountDeletionCancelled(user.Id), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendDeletionCancelledEmailAsync(default, default!, default);
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
        AccountDeletionCancelledProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new AccountDeletionCancelled(user.Id), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendDeletionCancelledEmailAsync(default, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_CancelledUser_SendsTheCourtesyNotice()
    {
        ApplicationUser user = CreateUser();
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        _emailSender.SendDeletionCancelledEmailAsync(user.Id, user.Email!, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        AccountDeletionCancelledProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new AccountDeletionCancelled(user.Id), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.Received(1)
            .SendDeletionCancelledEmailAsync(user.Id, user.Email!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_SendingFails_PropagatesTheFailure()
    {
        ApplicationUser user = CreateUser();
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        Error smtpError = new("Email.SendFailed", "SMTP relay refused the message.");
        _emailSender.SendDeletionCancelledEmailAsync(user.Id, user.Email!, Arg.Any<CancellationToken>())
            .Returns(Result.Failure(smtpError));
        AccountDeletionCancelledProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new AccountDeletionCancelled(user.Id), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(smtpError);
    }

    [Theory]
    [InlineData("this is not json at all")]
    [InlineData("{}")]
    [InlineData("""{"IdentityUserId":"not-a-guid"}""")]
    public void TryDeserialize_PoisonPayload_ReturnsTheNullVerdict(string poisonPayload)
    {
        AccountDeletionCancelledProcessor sut = CreateSut();

        object? message = sut.TryDeserialize(System.Text.Encoding.UTF8.GetBytes(poisonPayload));

        message.ShouldBeNull();
    }

    [Fact]
    public void TryDeserialize_ValidPayload_ReturnsTheTypedMessage()
    {
        Guid userId = Guid.CreateVersion7();
        AccountDeletionCancelledProcessor sut = CreateSut();

        object? message = sut.TryDeserialize(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new AccountDeletionCancelled(userId)));

        AccountDeletionCancelled typed = message.ShouldBeOfType<AccountDeletionCancelled>();
        typed.IdentityUserId.ShouldBe(userId);
    }

    private static ApplicationUser CreateUser() =>
        new()
        {
            Id = Guid.NewGuid(),
            UserName = "frodo",
            Email = "frodo@shire.me"
        };

    private AccountDeletionCancelledProcessor CreateSut() =>
        new(_userManager, _emailSender, NullLogger<AccountDeletionCancelledProcessor>.Instance);

    private static UserManager<ApplicationUser> CreateUserManager() =>
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);
}
