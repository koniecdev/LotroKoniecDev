using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Accounts;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using NSubstitute;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Features.Auth;

/// <summary>
/// What the confirm handler answers when the account moved after it loaded it: a second submit of the
/// same link finds the change already done, and anything else must not be reported as done (#869). The
/// rest of the flow is covered end to end by the integration suite.
/// </summary>
public sealed class ConfirmEmailChangeHandlerTests
{
    private const string CurrentEmail = "frodo@shire.me";
    private const string NewEmail = "frodo@rivendell.me";

    private readonly UserManager<ApplicationUser> _userManager = CreateUserManager();
    private readonly AuthDbContext _db = CreateDetachedDbContext();
    private readonly IUserSessionRevoker _sessionRevoker = Substitute.For<IUserSessionRevoker>();

    public ConfirmEmailChangeHandlerTests()
    {
        _userManager.NormalizeEmail(Arg.Any<string?>())
            .Returns(callInfo => callInfo.Arg<string?>()?.ToUpperInvariant());
    }

    [Fact]
    public async Task Handle_TheSameLinkLandedJustBeforeTheAddressCheck_AnswersDoneAndEndsTheSessions()
    {
        // A double click: the first submit lands after this one loaded the account, so the address lookup
        // finds this very account.
        ApplicationUser user = CreateUser();
        StubUser(user);
        _userManager.FindByEmailAsync(NewEmail).Returns(user);
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user, CreateAppliedCopyOf(user));
        ConfirmEmailChange.Handler sut = CreateSut();

        SharedKernel.Monads.Result result = await sut.Handle(CommandFor(user.Id), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();

        // The first submit may have been aborted before its own revocation ran, and nothing in the return
        // value shows whether this one ended the sessions, so it is asserted here.
        await _sessionRevoker.Received(1).RevokeAllAsync(user.Id.ToString(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TheSameLinkLandedJustBeforeTheSave_AnswersDoneAndEndsTheSessions()
    {
        // The first submit's save moved the concurrency stamp, so this one is refused, but the change is done.
        ApplicationUser user = CreateUser();
        StubUser(user);
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user, CreateAppliedCopyOf(user));
        _userManager.UpdateAsync(user).Returns(ConcurrencyFailure());
        ConfirmEmailChange.Handler sut = CreateSut();

        SharedKernel.Monads.Result result = await sut.Handle(CommandFor(user.Id), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _sessionRevoker.Received(1).RevokeAllAsync(user.Id.ToString(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AnotherSaveLandedJustBeforeTheSave_ReportsAFailedSave()
    {
        // A failed login saved the account in between. Nothing moved, so the visitor is asked to try again.
        ApplicationUser user = CreateUser();
        StubUser(user);
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user, CreateUser(user.Id));
        _userManager.UpdateAsync(user).Returns(ConcurrencyFailure());
        ConfirmEmailChange.Handler sut = CreateSut();

        SharedKernel.Monads.Result result = await sut.Handle(CommandFor(user.Id), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Auth.EmailChangeFailed");
    }

    [Fact]
    public async Task Handle_AnUndoPutTheAccountOnTheSameAddress_RefusesAsASpentLink()
    {
        // The confirm link and the undo link can both sit in one mailbox. An undo that lands first also puts
        // the account on this address, but it clears the password, so "done" would hide that the password
        // is gone. Its new security stamp has spent this link anyway.
        ApplicationUser user = CreateUser();
        StubUser(user);
        _userManager.FindByEmailAsync(NewEmail).Returns(user);
        ApplicationUser undone = CreateAppliedCopyOf(user);
        undone.PasswordHash = null;
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user, undone);
        ConfirmEmailChange.Handler sut = CreateSut();

        SharedKernel.Monads.Result result = await sut.Handle(CommandFor(user.Id), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Auth.InvalidEmailChangeToken");
    }

    [Fact]
    public async Task Handle_AnotherAccountOwnsTheAddress_RefusesAsTaken()
    {
        ApplicationUser user = CreateUser();
        StubUser(user);
        _userManager.FindByEmailAsync(NewEmail).Returns(CreateUser());
        ConfirmEmailChange.Handler sut = CreateSut();

        SharedKernel.Monads.Result result = await sut.Handle(CommandFor(user.Id), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Auth.UserAlreadyExistsByEmail");
    }

    private static ConfirmEmailChange.Command CommandFor(Guid userId) =>
        new(userId.ToString(), NewEmail, "confirm-token", "203.0.113.7", "xunit");

    private void StubUser(ApplicationUser user)
    {
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        _userManager.FindByEmailAsync(NewEmail).Returns((ApplicationUser?)null);
        _userManager.VerifyUserTokenAsync(
                user,
                EmailChangeTokenProvider.ProviderName,
                EmailChangeTokenProvider.PurposeFor(NewEmail),
                Arg.Any<string>())
            .Returns(true);
        _userManager.UpdateAsync(user).Returns(IdentityResult.Success);
    }

    private static IdentityResult ConcurrencyFailure() =>
        IdentityResult.Failed(new IdentityError
        {
            Code = nameof(IdentityErrorDescriber.ConcurrencyFailure),
            Description = "Optimistic concurrency failure."
        });

    private ConfirmEmailChange.Handler CreateSut() =>
        new(
            _userManager,
            _db,
            new OutboxWriter(_db, new OutboxSignal(), TimeProvider.System),
            _sessionRevoker,
            Substitute.For<IEmailChangeRevertReservation>(),
            TimeProvider.System,
            new ConfirmEmailChange.CommandValidator(),
            NullLogger<ConfirmEmailChange.Handler>.Instance);

    private static ApplicationUser CreateUser(Guid? id = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            UserName = "frodo",
            Email = CurrentEmail,
            EmailConfirmed = true,
            PasswordHash = "hashed",
            SecurityStamp = Guid.NewGuid().ToString()
        };

    /// <summary>The row a landed confirm leaves behind: the new address, confirmed, password kept.</summary>
    private static ApplicationUser CreateAppliedCopyOf(ApplicationUser user)
    {
        ApplicationUser applied = CreateUser(user.Id);
        applied.Email = NewEmail;
        return applied;
    }

    /// <summary>
    /// The handler only ever calls <c>ChangeTracker.Clear()</c> and adds the outbox row to this, which
    /// needs no connection, so the suite stays pure.
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
