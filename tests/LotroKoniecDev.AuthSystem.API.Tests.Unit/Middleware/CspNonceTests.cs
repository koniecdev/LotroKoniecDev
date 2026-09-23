using LotroKoniecDev.AuthSystem.API.Middleware;
using Microsoft.AspNetCore.Http;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Middleware;

public sealed class CspNonceTests
{
    [Fact]
    public void Get_ShouldReturnTheNonceThatWasIssued()
    {
        // Arrange
        DefaultHttpContext context = new();
        string issued = CspNonce.Issue(context);

        // Act
        string? nonce = CspNonce.Get(context);

        // Assert
        nonce.ShouldBe(issued);
    }

    [Fact]
    public void Get_ShouldBeNull_WhenNoNonceWasIssued()
    {
        // Arrange: Development runs without the security headers, so no nonce is ever issued
        DefaultHttpContext context = new();

        // Act
        string? nonce = CspNonce.Get(context);

        // Assert
        nonce.ShouldBeNull();
    }
}
