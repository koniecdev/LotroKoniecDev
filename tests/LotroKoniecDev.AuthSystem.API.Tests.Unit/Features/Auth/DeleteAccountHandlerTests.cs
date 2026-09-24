using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Tests.Unit.Shared;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Monads;
using NSubstitute;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Features.Auth;

/// <summary>
/// Where the deletion schedule permit is taken (#811). It must be the last check before the save, so only
/// a schedule that would happen spends one (ADR-0055). The budget is a real one-permit throttle, so each
/// test reads what is left of it instead of how the handler called it.
/// </summary>
public sealed class DeleteAccountHandlerTests : IDisposable
{
    private const string Password = "Correct-Horse-1!";

    private readonly UserManager<ApplicationUser> _userManager = CreateUserManager();
    private readonly AuthDbContext _db = CreateDetachedDbContext();
    private readonly PerAccountFixedWindowThrottle _confirmationThrottle =
        new(AccountBudgets.PasswordConfirmationPermitLimit, AccountBudgets.Window);
    private readonly PerAccountFixedWindowThrottle _scheduleThrottle =
        new(permitLimit: 1, AccountBudgets.DeletionScheduleWindow);
    private readonly CapturingLogger<DeleteAccount.Handler> _logger = new();

    public void Dispose()
    {
        _confirmationThrottle.Dispose();
        _scheduleThrottle.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task Handle_CorrectPassword_SchedulesAndSpendsTheSchedulePermit()
    {
        ApplicationUser user = StubUser(passwordValid: true);

        Result<DeleteAccount.ScheduledDeletion> result = await CreateSut().Handle(CommandFor(user), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        user.DeletionScheduledAt.ShouldNotBeNull();
        _scheduleThrottle.TryAcquire(user.Id).ShouldBeFalse();
    }

    [Fact]
    public async Task Handle_WrongPassword_LeavesTheSchedulePermitUnspent()
    {
        ApplicationUser user = StubUser(passwordValid: false);

        Result<DeleteAccount.ScheduledDeletion> result = await CreateSut().Handle(CommandFor(user), CancellationToken.None);

        result.Error.Code.ShouldBe("Auth.InvalidCurrentPassword");
        _scheduleThrottle.TryAcquire(user.Id).ShouldBeTrue();
    }

    [Fact]
    public async Task Handle_DeletionAlreadyScheduled_LeavesTheSchedulePermitUnspent()
    {
        ApplicationUser user = StubUser(passwordValid: true);
        user.DeletionScheduledAt = DateTimeOffset.UtcNow.AddDays(-1);

        Result<DeleteAccount.ScheduledDeletion> result = await CreateSut().Handle(CommandFor(user), CancellationToken.None);

        result.Error.Code.ShouldBe("Auth.DeletionAlreadyScheduled");
        _scheduleThrottle.TryAcquire(user.Id).ShouldBeTrue();
    }

    [Fact]
    public async Task Handle_SchedulePermitSpent_RefusesWithoutSavingAnythingAndLogsTheAccount()
    {
        ApplicationUser user = StubUser(passwordValid: true);
        _scheduleThrottle.TryAcquire(user.Id).ShouldBeTrue();

        Result<DeleteAccount.ScheduledDeletion> result = await CreateSut().Handle(CommandFor(user), CancellationToken.None);

        result.Error.Code.ShouldBe("Auth.DeletionScheduleThrottled");
        user.DeletionScheduledAt.ShouldBeNull();
        user.LockoutEnd.ShouldBeNull();
        _db.ChangeTracker.Entries().ShouldBeEmpty();
        await _userManager.DidNotReceiveWithAnyArgs().UpdateAsync(default!);

        CapturingLogger<DeleteAccount.Handler>.LogEntry entry = _logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.EventId.ShouldBe(EventIds.GdprDeletionScheduleThrottled);
        entry.Message.ShouldContain(user.Id.ToString());
    }

    private ApplicationUser StubUser(bool passwordValid)
    {
        ApplicationUser user = new()
        {
            Id = Guid.NewGuid(),
            UserName = "frodo",
            Email = "frodo@shire.me",
            PasswordHash = "hashed",
            SecurityStamp = Guid.NewGuid().ToString()
        };

        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        _userManager.CheckPasswordAsync(user, Arg.Any<string>()).Returns(passwordValid);
        _userManager.UpdateAsync(user).Returns(IdentityResult.Success);
        return user;
    }

    private static DeleteAccount.Command CommandFor(ApplicationUser user) =>
        new(user.Id.ToString(), Password, "203.0.113.7", "xunit");

    private DeleteAccount.Handler CreateSut() =>
        new(
            _userManager,
            Substitute.For<IOpenIddictTokenManager>(),
            Substitute.For<IOpenIddictAuthorizationManager>(),
            new OutboxWriter(_db, new OutboxSignal(), TimeProvider.System),
            Substitute.For<IAccountDeletionSchedule>(),
            _confirmationThrottle,
            _scheduleThrottle,
            TimeProvider.System,
            new DeleteAccount.CommandValidator(),
            _logger);

    /// <summary>
    /// The handler only adds the outbox row to this context. That needs no connection, because the save
    /// runs through the substituted <see cref="UserManager{TUser}"/>, so the suite stays pure.
    /// </summary>
    private static AuthDbContext CreateDetachedDbContext() =>
        new(new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql("Host=unit-test;Database=none;Username=none;Password=none")
            .Options);

    private static UserManager<ApplicationUser> CreateUserManager() =>
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);
}
