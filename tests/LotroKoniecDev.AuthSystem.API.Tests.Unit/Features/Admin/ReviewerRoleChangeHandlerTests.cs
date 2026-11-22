using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using LotroKoniecDev.AuthSystem.API.Features.Admin;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.Monads;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Features.Admin;

/// <summary>
/// The branches no endpoint test reaches for sure: the role write fails (#1488). When a fresh read shows
/// the target state, an overlapping call made the change and ended the sessions; otherwise the change did
/// not happen. Either way this call ends no session, and the revoker is asserted because that leaves no
/// trace in the result.
/// </summary>
public sealed class ReviewerRoleChangeHandlerTests
{
    private readonly UserManager<ApplicationUser> _userManager = CreateUserManager();
    private readonly IUserSessionRevoker _sessionRevoker = Substitute.For<IUserSessionRevoker>();
    private readonly ApplicationUser _account = new() { Id = Guid.NewGuid(), UserName = "frodo" };

    [Fact]
    public async Task Grant_WhenIdentityRefusesTheWrite_FailsAndEndsNoSession()
    {
        // Arrange
        StubAccount(AuthConstants.Roles.Translator);
        _userManager.AddToRoleAsync(_account, AuthConstants.Roles.Reviewer)
            .Returns(IdentityResult.Failed(new IdentityError { Code = "ConcurrencyFailure" }));
        GrantReviewerRole.Handler handler = new(
            _userManager, _sessionRevoker, new GrantReviewerRole.CommandValidator(), NullLogger<GrantReviewerRole.Handler>.Instance);

        // Act
        Result result = await handler.Handle(new GrantReviewerRole.Command("admin", _account.Id), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Auth.ReviewerRoleChangeFailed");
        await _sessionRevoker.DidNotReceiveWithAnyArgs().RevokeAllAsync(default!);
    }

    [Fact]
    public async Task Remove_WhenIdentityRefusesTheWrite_FailsAndEndsNoSession()
    {
        // Arrange
        StubAccount(AuthConstants.Roles.Translator, AuthConstants.Roles.Reviewer);
        _userManager.IsInRoleAsync(_account, AuthConstants.Roles.Reviewer).Returns(true);
        _userManager.RemoveFromRoleAsync(_account, AuthConstants.Roles.Reviewer)
            .Returns(IdentityResult.Failed(new IdentityError { Code = "ConcurrencyFailure" }));
        RemoveReviewerRole.Handler handler = new(
            _userManager, _sessionRevoker, new RemoveReviewerRole.CommandValidator(), NullLogger<RemoveReviewerRole.Handler>.Instance);

        // Act
        Result result = await handler.Handle(new RemoveReviewerRole.Command("admin", _account.Id), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Auth.ReviewerRoleChangeFailed");
        await _sessionRevoker.DidNotReceiveWithAnyArgs().RevokeAllAsync(default!);
    }

    [Fact]
    public async Task Grant_WhenTheWriteFailsButAFreshReadShowsTheRole_SucceedsAndEndsNoSession()
    {
        // Arrange
        _userManager.FindByIdAsync(_account.Id.ToString()).Returns(_account);
        _userManager.GetRolesAsync(_account).Returns(
            [AuthConstants.Roles.Translator],
            [AuthConstants.Roles.Translator, AuthConstants.Roles.Reviewer]);
        _userManager.AddToRoleAsync(_account, AuthConstants.Roles.Reviewer)
            .Returns(IdentityResult.Failed(new IdentityError { Code = "ConcurrencyFailure" }));
        GrantReviewerRole.Handler handler = CreateGrantHandler();

        // Act
        Result result = await handler.Handle(new GrantReviewerRole.Command("admin", _account.Id), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        await _sessionRevoker.DidNotReceiveWithAnyArgs().RevokeAllAsync(default!);
    }

    [Fact]
    public async Task Grant_WhenTheWriteThrowsButAFreshReadShowsTheRole_SucceedsAndEndsNoSession()
    {
        // Arrange: the duplicate role row of an overlapping call.
        _userManager.FindByIdAsync(_account.Id.ToString()).Returns(_account);
        _userManager.GetRolesAsync(_account).Returns(
            [AuthConstants.Roles.Translator],
            [AuthConstants.Roles.Translator, AuthConstants.Roles.Reviewer]);
        _userManager.AddToRoleAsync(_account, AuthConstants.Roles.Reviewer)
            .ThrowsAsync(new DbUpdateException("duplicate key"));
        GrantReviewerRole.Handler handler = CreateGrantHandler();

        // Act
        Result result = await handler.Handle(new GrantReviewerRole.Command("admin", _account.Id), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        await _sessionRevoker.DidNotReceiveWithAnyArgs().RevokeAllAsync(default!);
    }

    [Fact]
    public async Task Grant_WhenTheWriteThrowsAndAFreshReadLacksTheRole_LetsTheExceptionThrough()
    {
        // Arrange
        StubAccount(AuthConstants.Roles.Translator);
        DbUpdateException failure = new("write failed");
        _userManager.AddToRoleAsync(_account, AuthConstants.Roles.Reviewer).ThrowsAsync(failure);
        GrantReviewerRole.Handler handler = CreateGrantHandler();

        // Act
        DbUpdateException thrown = await Should.ThrowAsync<DbUpdateException>(async () =>
            await handler.Handle(new GrantReviewerRole.Command("admin", _account.Id), CancellationToken.None));

        // Assert
        thrown.ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task Remove_WhenTheWriteFailsButAFreshReadLacksTheRole_SucceedsAndEndsNoSession()
    {
        // Arrange
        StubAccount(AuthConstants.Roles.Translator);
        _userManager.IsInRoleAsync(_account, AuthConstants.Roles.Reviewer).Returns(true);
        _userManager.RemoveFromRoleAsync(_account, AuthConstants.Roles.Reviewer)
            .Returns(IdentityResult.Failed(new IdentityError { Code = "ConcurrencyFailure" }));
        RemoveReviewerRole.Handler handler = CreateRemoveHandler();

        // Act
        Result result = await handler.Handle(new RemoveReviewerRole.Command("admin", _account.Id), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        await _sessionRevoker.DidNotReceiveWithAnyArgs().RevokeAllAsync(default!);
    }

    [Fact]
    public async Task Remove_WhenTheWriteThrowsButAFreshReadLacksTheRole_SucceedsAndEndsNoSession()
    {
        // Arrange: the role row an overlapping call already deleted.
        StubAccount(AuthConstants.Roles.Translator);
        _userManager.IsInRoleAsync(_account, AuthConstants.Roles.Reviewer).Returns(true);
        _userManager.RemoveFromRoleAsync(_account, AuthConstants.Roles.Reviewer)
            .ThrowsAsync(new DbUpdateConcurrencyException("0 rows affected"));
        RemoveReviewerRole.Handler handler = CreateRemoveHandler();

        // Act
        Result result = await handler.Handle(new RemoveReviewerRole.Command("admin", _account.Id), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        await _sessionRevoker.DidNotReceiveWithAnyArgs().RevokeAllAsync(default!);
    }

    [Fact]
    public async Task Remove_WhenTheWriteThrowsAndAFreshReadStillShowsTheRole_LetsTheExceptionThrough()
    {
        // Arrange
        StubAccount(AuthConstants.Roles.Translator, AuthConstants.Roles.Reviewer);
        _userManager.IsInRoleAsync(_account, AuthConstants.Roles.Reviewer).Returns(true);
        DbUpdateException failure = new("write failed");
        _userManager.RemoveFromRoleAsync(_account, AuthConstants.Roles.Reviewer).ThrowsAsync(failure);
        RemoveReviewerRole.Handler handler = CreateRemoveHandler();

        // Act
        DbUpdateException thrown = await Should.ThrowAsync<DbUpdateException>(async () =>
            await handler.Handle(new RemoveReviewerRole.Command("admin", _account.Id), CancellationToken.None));

        // Assert
        thrown.ShouldBeSameAs(failure);
    }

    private void StubAccount(params string[] roles)
    {
        _userManager.FindByIdAsync(_account.Id.ToString()).Returns(_account);
        _userManager.GetRolesAsync(_account).Returns(roles.ToList());
    }

    private GrantReviewerRole.Handler CreateGrantHandler() =>
        new(_userManager, _sessionRevoker, new GrantReviewerRole.CommandValidator(), NullLogger<GrantReviewerRole.Handler>.Instance);

    private RemoveReviewerRole.Handler CreateRemoveHandler() =>
        new(_userManager, _sessionRevoker, new RemoveReviewerRole.CommandValidator(), NullLogger<RemoveReviewerRole.Handler>.Instance);

    private static UserManager<ApplicationUser> CreateUserManager() =>
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);
}
