using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.API.Services.RateLimiting;
using LotroKoniecDev.TranslationSystem.API.Settings;
using Microsoft.AspNetCore.Http;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Services.RateLimiting;

/// <summary>
/// Pins the bucket of the general per-minute limit (#1488): a signed token with Reviewer counts per account
/// at 300, the admin has no limit, and every other caller keeps the address key of ADR-0054 at 100.
/// </summary>
public sealed class RequestRateLimitPartitionerTests
{
    private const string CallerAddress = "203.0.113.7";
    private const string AuthenticationType = "Bearer";

    private static readonly Guid AccountId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    [Fact]
    public void Choose_ForASignedReviewerToken_CountsPerAccountAtThreeHundred()
    {
        // Arrange
        RequestRateLimitPartitioner partitioner = CreatePartitioner();
        HttpContext httpContext = ContextFor(
            AuthenticatedUser(AccountId.ToString(), AuthConstants.Roles.Translator, AuthConstants.Roles.Reviewer));

        // Act
        (string key, int? permitLimit) = partitioner.Choose(httpContext);

        // Assert
        key.ShouldBe($"account:{AccountId}");
        permitLimit.ShouldBe(300);
    }

    [Theory]
    [InlineData(AuthConstants.Roles.Admin)]
    [InlineData(AuthConstants.Roles.Admin, AuthConstants.Roles.Reviewer)]
    public void Choose_ForASignedAdminToken_SetsNoLimit(params string[] roles)
    {
        // Arrange
        RequestRateLimitPartitioner partitioner = CreatePartitioner();
        HttpContext httpContext = ContextFor(
            AuthenticatedUser(AccountId.ToString(), [AuthConstants.Roles.Translator, .. roles]));

        // Act
        (string key, int? permitLimit) = partitioner.Choose(httpContext);

        // Assert
        key.ShouldBe($"account:{AccountId}");
        permitLimit.ShouldBeNull();
    }

    [Fact]
    public void Partition_ForASignedAdminToken_NeverRefusesAPermit()
    {
        // Arrange
        RequestRateLimitPartitioner partitioner = CreatePartitioner();
        HttpContext httpContext = ContextFor(
            AuthenticatedUser(AccountId.ToString(), AuthConstants.Roles.Translator, AuthConstants.Roles.Admin));
        RateLimitPartition<string> partition = partitioner.Partition(httpContext);
        using RateLimiter limiter = partition.Factory(partition.PartitionKey);

        // Act
        bool[] acquired = Enumerable.Range(0, 1000)
            .Select(_ =>
            {
                using RateLimitLease lease = limiter.AttemptAcquire();
                return lease.IsAcquired;
            })
            .ToArray();

        // Assert
        acquired.ShouldAllBe(isAcquired => isAcquired);
    }

    [Fact]
    public void Partition_ForASignedReviewerToken_RefusesThePermitAfterThreeHundred()
    {
        // Arrange
        RequestRateLimitPartitioner partitioner = CreatePartitioner();
        HttpContext httpContext = ContextFor(
            AuthenticatedUser(AccountId.ToString(), AuthConstants.Roles.Translator, AuthConstants.Roles.Reviewer));
        RateLimitPartition<string> partition = partitioner.Partition(httpContext);
        using RateLimiter limiter = partition.Factory(partition.PartitionKey);
        for (int i = 0; i < 300; i++)
        {
            using RateLimitLease lease = limiter.AttemptAcquire();
            if (!lease.IsAcquired)
            {
                throw new InvalidOperationException($"Permit {i + 1} was refused before the bucket was spent.");
            }
        }

        // Act
        using RateLimitLease overTheLimit = limiter.AttemptAcquire();

        // Assert
        overTheLimit.IsAcquired.ShouldBeFalse();
    }

    [Theory]
    [InlineData(AuthConstants.Roles.Translator)]
    [InlineData("NotARole")]
    public void Choose_ForASignedTokenWithoutReviewerOrAdmin_CountsPerAddressAtOneHundred(string role)
    {
        // Arrange
        RequestRateLimitPartitioner partitioner = CreatePartitioner();
        HttpContext httpContext = ContextFor(AuthenticatedUser(AccountId.ToString(), role));

        // Act
        (string key, int? permitLimit) = partitioner.Choose(httpContext);

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
        (string key, int? permitLimit) = partitioner.Choose(httpContext);

        // Assert
        key.ShouldBe(CallerAddress);
        permitLimit.ShouldBe(100);
    }

    [Theory]
    [InlineData(AuthConstants.Roles.Reviewer)]
    [InlineData(AuthConstants.Roles.Admin)]
    public void Choose_ForReviewerOrAdminClaimsOnAnIdentityThatDidNotAuthenticate_CountsPerAddress(string role)
    {
        // Arrange: only a token whose signature checked out may name an account.
        RequestRateLimitPartitioner partitioner = CreatePartitioner();
        ClaimsIdentity unauthenticated = new(
            [new Claim("sub", AccountId.ToString()), new Claim("role", role)],
            authenticationType: null,
            nameType: "name",
            roleType: "role");
        HttpContext httpContext = ContextFor(new ClaimsPrincipal(unauthenticated));

        // Act
        (string key, int? permitLimit) = partitioner.Choose(httpContext);

        // Assert
        key.ShouldBe(CallerAddress);
        permitLimit.ShouldBe(100);
    }

    [Theory]
    [InlineData(null, AuthConstants.Roles.Reviewer)]
    [InlineData("", AuthConstants.Roles.Reviewer)]
    [InlineData("not-a-guid", AuthConstants.Roles.Reviewer)]
    [InlineData(null, AuthConstants.Roles.Admin)]
    [InlineData("not-a-guid", AuthConstants.Roles.Admin)]
    public void Choose_ForAReviewerOrAdminWithoutAUsableSubject_CountsPerAddress(string? subject, string role)
    {
        // Arrange: never a shared bucket, or no limit, for a token without an id.
        RequestRateLimitPartitioner partitioner = CreatePartitioner();
        HttpContext httpContext = ContextFor(AuthenticatedUser(subject, role));

        // Act
        (string key, int? permitLimit) = partitioner.Choose(httpContext);

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
