using LotroKoniecDev.AuthSystem.API.Features.Auth;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Features.Auth;

/// <summary>
/// The "expired" warning (#977) is told apart from a revoke only by where the two notes sit in OpenIddict's
/// pipeline. If an OpenIddict update puts another check between them, that check's refusals would read as
/// "expired" in the log. This test fails first.
/// </summary>
public sealed class OpenIddictTokenRefusalsTests
{
    [Fact]
    public void ExpiryNotes_ShouldEncloseOnlyOpenIddictsExpiryCheck()
    {
        // Arrange
        int firstNote = OpenIddictTokenRefusals.NoteTokenUser.Descriptor.Order;
        int secondNote = OpenIddictTokenRefusals.NoteLifetimeChecked.Descriptor.Order;

        // Act
        List<Type> enclosedHandlers = OpenIddictServerHandlers.DefaultHandlers
            .Concat(OpenIddictServerAspNetCoreHandlers.DefaultHandlers)
            .Where(descriptor => descriptor.ContextType == typeof(OpenIddictServerEvents.ValidateTokenContext))
            .Where(descriptor => descriptor.Order > firstNote && descriptor.Order < secondNote)
            .Select(descriptor => descriptor.ServiceDescriptor.ServiceType)
            .ToList();

        // Assert
        enclosedHandlers.ShouldBe([typeof(OpenIddictServerHandlers.Protection.ValidateExpirationDate)]);
    }
}
