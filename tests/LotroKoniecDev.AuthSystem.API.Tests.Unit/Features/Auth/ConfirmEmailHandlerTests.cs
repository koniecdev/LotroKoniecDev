using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;
using NSubstitute;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Features.Auth;

/// <summary>
/// Each handler that hides whether an address has an account waits for its floor in a finally
/// (ADR-0059 §2). This one stands for all of them: a lookup that throws must not answer early either.
/// </summary>
public sealed class ConfirmEmailHandlerTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Handle_ShouldStillWaitForTheFloor_WhenTheLookupThrows()
    {
        // Arrange
        FakeTimeProvider clock = new();
        UserManager<ApplicationUser> userManager = Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);
        userManager.FindByEmailAsync(Arg.Any<string>())
            .Returns(Task.FromException<ApplicationUser?>(new InvalidOperationException("The database is down.")));
        ConfirmEmail.Handler sut = new(
            userManager,
            new ResponseTimeFloor(clock),
            new ConfirmEmail.CommandValidator(),
            NullLogger<ConfirmEmail.Handler>.Instance);

        // Act
        Task<Result> handling = sut.Handle(new ConfirmEmail.Command("frodo@shire.me", "token"), CancellationToken.None).AsTask();
        bool answeredBeforeTheFloor = handling.IsCompleted;
        clock.Advance(ResponseTimeFloors.AccountLookup);

        // Assert
        answeredBeforeTheFloor.ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => handling.WaitAsync(CompletionTimeout));
    }
}
