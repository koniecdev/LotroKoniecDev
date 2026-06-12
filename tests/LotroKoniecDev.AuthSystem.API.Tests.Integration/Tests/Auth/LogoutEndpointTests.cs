using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed class LogoutEndpointTests : EndpointsTestBase
{
    public LogoutEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task Logout_ShouldReturnSuccessOrRedirect()
    {
        // Act
        HttpResponseMessage response = await ApiClient.Http.GetAsync(
            new Uri("connect/logout", UriKind.Relative));

        // Assert - logout should not return an error
        int statusCode = (int)response.StatusCode;
        statusCode.ShouldBeGreaterThanOrEqualTo(200);
        statusCode.ShouldBeLessThan(400);
    }
}
