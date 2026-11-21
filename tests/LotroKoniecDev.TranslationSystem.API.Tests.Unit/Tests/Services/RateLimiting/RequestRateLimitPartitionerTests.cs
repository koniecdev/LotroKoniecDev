using System.Net;
using System.Security.Claims;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.API.Services.RateLimiting;
using LotroKoniecDev.TranslationSystem.API.Settings;
using Microsoft.AspNetCore.Http;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Services.RateLimiting;

/// <summary>
/// Pins the bucket of the general per-minute limit (#1488): a signed token with Reviewer or Admin counts
/// per account at 300, and every other caller keeps the address key of ADR-0054 at 100.
/// </summary>
public sealed class RequestRateLimitPartitionerTests
{
    private const string CallerAddress = "203.0.113.7";
    private const string AuthenticationType = "Bearer";

    private static readonly Guid AccountId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    [Theory]
    [InlineData(AuthConstants.Roles.Reviewer)]
    [InlineData(AuthConstants.Roles.Admin)]
    public void Choose_ForASignedTokenWithATrustedRole_CountsPerAccountAtThreeHundred(string trustedRole)
    {
        // Arrange
        RequestRateLimitPartitioner partitioner = CreatePartitioner();
        HttpContext httpContext = ContextFor(
            AuthenticatedUser(AccountId.ToString(), AuthConstants.Roles.Translator, trustedRole));

        // Act
        (string key, int permitLimit) = partitioner.Choose(httpContext);

        // Assert
        key.ShouldBe($"account:{AccountId}");
        permitLimit.ShouldBe(300);
    }

    [Theory]
    [InlineData(AuthConstants.Roles.Translator)]
    [InlineData("NotARole")]
    public void Choose_ForASignedTokenWithoutATrustedRole_CountsPerAddressAtOneHundred(string role)
    {
        // Arrange
        RequestRateLimitPartitioner partitioner = CreatePartitioner();
        HttpContext httpContext = ContextFor(AuthenticatedUser(AccountId.ToString(), role));

        // Act
        (string key, int permitLimit) = partitioner.Choose(httpContext);

        // Assert
        key.ShouldBe(CallerAddress);
        permitLimit.ShouldBe(100);
    }

    [Fact]
    public void Choose_ForAnAnonymousCaller_CountsPerAddressAtOneHundred()
    {
        // Arrange
        RequestRateLimitPartitioner partitioner = CreatePartitioner();
        HttpContext httpContext = ContextFor(new ClaimsPrincipal(new ClaimsIdentity()));

        // Act
        (string key, int permitLimit) = partitioner.Choose(httpContext);

        // Assert
        key.ShouldBe(CallerAddress);
        permitLimit.ShouldBe(100);
    }

    [Fact]
    public void Choose_ForTrustedRoleClaimsOnAnIdentityThatDidNotAuthenticate_CountsPerAddress()
    {
        // Arrange: only a token whose signature checked out may name an account.
        RequestRateLimitPartitioner partitioner = CreatePartitioner();
        ClaimsIdentity unauthenticated = new(
            [new Claim("sub", AccountId.ToString()), new Claim("role", AuthConstants.Roles.Reviewer)],
            authenticationType: null,
            nameType: "name",
            roleType: "role");
        HttpContext httpContext = ContextFor(new ClaimsPrincipal(unauthenticated));

        // Act
        (string key, int permitLimit) = partitioner.Choose(httpContext);

        // Assert
        key.ShouldBe(CallerAddress);
        permitLimit.ShouldBe(100);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void Choose_ForATrustedRoleWithoutAUsableSubject_CountsPerAddress(string? subject)
    {
        // Arrange: never a shared bucket for every token without an id.
        RequestRateLimitPartitioner partitioner = CreatePartitioner();
        HttpContext httpContext = ContextFor(AuthenticatedUser(subject, AuthConstants.Roles.Reviewer));

        // Act
        (string key, int permitLimit) = partitioner.Choose(httpContext);

        // Assert
        key.ShouldBe(CallerAddress);
        permitLimit.ShouldBe(100);
    }

    [Fact]
    public void Choose_ForTwoReviewersBehindOneAddress_GivesEachItsOwnKey()
    {
        // Arrange
        RequestRateLimitPartitioner partitioner = CreatePartitioner();
        HttpContext first = ContextFor(AuthenticatedUser(Guid.NewGuid().ToString(), AuthConstants.Roles.Reviewer));
        HttpContext second = ContextFor(AuthenticatedUser(Guid.NewGuid().ToString(), AuthConstants.Roles.Reviewer));

        // Act
        string firstKey = partitioner.Choose(first).Key;
        string secondKey = partitioner.Choose(second).Key;

        // Assert
        firstKey.ShouldNotBe(secondKey);
    }

    private static RequestRateLimitPartitioner CreatePartitioner() =>
        new(new RateLimitPartitionKeyResolver(
            Microsoft.Extensions.Options.Options.Create(new FrontendCallerSettings { Key = null })));

    private static ClaimsPrincipal AuthenticatedUser(string? subject, params string[] roles)
    {
        List<Claim> claims = [.. roles.Select(role => new Claim("role", role))];
        if (subject is not null)
        {
            claims.Add(new Claim("sub", subject));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType, nameType: "name", roleType: "role"));
    }

    private static HttpContext ContextFor(ClaimsPrincipal user)
    {
        DefaultHttpContext httpContext = new() { User = user };
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(CallerAddress);
        return httpContext;
    }
}
