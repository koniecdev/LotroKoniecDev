using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using NSubstitute;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Emails;

public sealed class AccountDeletionScheduledProcessorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly GdprSettings Gdpr = new();

    private readonly UserManager<ApplicationUser> _userManager = CreateUserManager();
    private readonly IAccountDeletionEmailSender _emailSender =
        Substitute.For<IAccountDeletionEmailSender>();
    private readonly TimeProvider _timeProvider = CreateTimeProvider();

    [Fact]
    public async Task ProcessAsync_UserNoLongerExists_SucceedsWithoutSending()
    {
        Guid userId = Guid.NewGuid();
        _userManager.FindByIdAsync(userId.ToString()).Returns((ApplicationUser?)null);
        AccountDeletionScheduledProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new AccountDeletionScheduled(userId), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendDeletionScheduledEmailAsync(default, default!, default!, default, default);
    }

    [Fact]
    public async Task ProcessAsync_ScheduleWasCancelledInTheGap_SucceedsWithoutSending()
    {
        // The drift guard (ADR-0038 decision 2): a cancellation racing this message wins — a
        // stale "your account will be deleted" must never go out, and redelivery cannot change
        // the outcome.
        ApplicationUser user = CreateUser();
        user.DeletionScheduledAt = null;
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        AccountDeletionScheduledProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new AccountDeletionScheduled(user.Id), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendDeletionScheduledEmailAsync(default, default!, default!, default, default);
    }

    [Fact]
    public async Task ProcessAsync_GraceWindowAlreadyOver_SucceedsWithoutSending()
    {
        // A DLQ replay long after the fact: erasure keeps DeletionScheduledAt as its audit trace,
        // so the schedule-gone guard alone would let this mint a cancel token for an anonymized
        // account. Once the window is over the e-mail is a lie either way.
        ApplicationUser user = CreateUser();
        user.DeletionScheduledAt = Now - Gdpr.DeletionGracePeriod - TimeSpan.FromDays(1);
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        AccountDeletionScheduledProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new AccountDeletionScheduled(user.Id), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendDeletionScheduledEmailAsync(default, default!, default!, default, default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProcessAsync_UserHasNoEmailAddress_SucceedsWithoutSending(string? email)
    {
        ApplicationUser user = CreateUser();
        user.DeletionScheduledAt = Now;
        user.Email = email;
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        AccountDeletionScheduledProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new AccountDeletionScheduled(user.Id), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendDeletionScheduledEmailAsync(default, default!, default!, default, default);
    }

    [Fact]
    public async Task ProcessAsync_ScheduledUser_SendsTheEmailWithAFreshTokenAndTheRecomputedDate()
    {
        ApplicationUser user = CreateUser();
        user.DeletionScheduledAt = Now - TimeSpan.FromHours(1);
        DateTimeOffset expectedFinalizesAt = user.DeletionScheduledAt.Value + Gdpr.DeletionGracePeriod;
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        _userManager.GenerateUserTokenAsync(
                user,
                AccountDeletionCancellationTokenProvider.ProviderName,
                AccountDeletionCancellationTokenProvider.CancelDeletionPurpose)
            .Returns("fresh-cancel-token");
        _emailSender.SendDeletionScheduledEmailAsync(
                user.Id, user.Email!, "fresh-cancel-token", expectedFinalizesAt, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        AccountDeletionScheduledProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new AccountDeletionScheduled(user.Id), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.Received(1).SendDeletionScheduledEmailAsync(
            user.Id, user.Email!, "fresh-cancel-token", expectedFinalizesAt, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_SendingFails_PropagatesTheFailure()
    {
        ApplicationUser user = CreateUser();
        user.DeletionScheduledAt = Now;
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        _userManager.GenerateUserTokenAsync(
                user,
                AccountDeletionCancellationTokenProvider.ProviderName,
                AccountDeletionCancellationTokenProvider.CancelDeletionPurpose)
            .Returns("fresh-cancel-token");
        Error smtpError = new("Email.SendFailed", "SMTP relay refused the message.");
        _emailSender.SendDeletionScheduledEmailAsync(
                user.Id, user.Email!, "fresh-cancel-token", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(smtpError));
        AccountDeletionScheduledProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(new AccountDeletionScheduled(user.Id), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(smtpError);
    }

    [Theory]
    [InlineData("this is not json at all")]
    [InlineData("{}")]
    [InlineData("""{"IdentityUserId":"not-a-guid"}""")]
    public void TryDeserialize_PoisonPayload_ReturnsTheNullVerdict(string poisonPayload)
    {
        AccountDeletionScheduledProcessor sut = CreateSut();

        object? message = sut.TryDeserialize(System.Text.Encoding.UTF8.GetBytes(poisonPayload));

        message.ShouldBeNull();
    }

    [Fact]
    public void TryDeserialize_ValidPayload_ReturnsTheTypedMessage()
    {
        Guid userId = Guid.CreateVersion7();
        AccountDeletionScheduledProcessor sut = CreateSut();

        object? message = sut.TryDeserialize(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new AccountDeletionScheduled(userId)));

        AccountDeletionScheduled typed = message.ShouldBeOfType<AccountDeletionScheduled>();
        typed.IdentityUserId.ShouldBe(userId);
    }

    private static ApplicationUser CreateUser() =>
        new()
        {
            Id = Guid.NewGuid(),
            UserName = "frodo",
            Email = "frodo@shire.me"
        };

    private AccountDeletionScheduledProcessor CreateSut() =>
        new(
            _userManager,
            _emailSender,
            _timeProvider,
            Microsoft.Extensions.Options.Options.Create(Gdpr),
            NullLogger<AccountDeletionScheduledProcessor>.Instance);

    private static TimeProvider CreateTimeProvider()
    {
        TimeProvider timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(Now);
        return timeProvider;
    }

    private static UserManager<ApplicationUser> CreateUserManager() =>
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);
}
