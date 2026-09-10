using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using LotroKoniecDev.AuthSystem.API.Extensions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Extensions;

/// <summary>
/// The convention behind the Razor group's default limit: add the default where nothing was said, keep
/// its hands off a page that decided for itself. The ordering trap that makes the second half necessary
/// is explained on <see cref="RateLimitingEndpointConventions"/>.
/// </summary>
public sealed class RateLimitingEndpointConventionsTests
{
    private const string DefaultPolicy = "auth-page-limit";

    [Fact]
    public void ApplyDefaultPolicy_ShouldAddTheDefault_WhenTheEndpointSaidNothing()
    {
        // Arrange
        RouteEndpointBuilder endpointBuilder = CreateEndpointBuilder();

        // Act
        RateLimitingEndpointConventions.ApplyDefaultPolicy(endpointBuilder, DefaultPolicy);

        // Assert
        endpointBuilder.Metadata.OfType<EnableRateLimitingAttribute>()
            .Select(attribute => attribute.PolicyName)
            .ShouldBe([DefaultPolicy]);
    }

    [Fact]
    public void ApplyDefaultPolicy_ShouldLeaveAPageOwnPolicyAlone()
    {
        // Arrange: what [EnableRateLimiting("resend-confirmation-limit")] puts on the endpoint
        RouteEndpointBuilder endpointBuilder = CreateEndpointBuilder();
        endpointBuilder.Metadata.Add(new EnableRateLimitingAttribute("resend-confirmation-limit"));

        // Act
        RateLimitingEndpointConventions.ApplyDefaultPolicy(endpointBuilder, DefaultPolicy);

        // Assert: one policy, still the page's own — a second one would be the last match and would win
        endpointBuilder.Metadata.OfType<EnableRateLimitingAttribute>()
            .Select(attribute => attribute.PolicyName)
            .ShouldBe(["resend-confirmation-limit"]);
    }

    [Fact]
    public void ApplyDefaultPolicy_ShouldLeaveADeliberateOptOutAlone()
    {
        // Arrange
        RouteEndpointBuilder endpointBuilder = CreateEndpointBuilder();
        endpointBuilder.Metadata.Add(new DisableRateLimitingAttribute());

        // Act
        RateLimitingEndpointConventions.ApplyDefaultPolicy(endpointBuilder, DefaultPolicy);

        // Assert
        endpointBuilder.Metadata.OfType<EnableRateLimitingAttribute>().ShouldBeEmpty();
        endpointBuilder.Metadata.OfType<DisableRateLimitingAttribute>().Count().ShouldBe(1);
    }

    [Fact]
    public void RequireRateLimitingByDefault_ShouldRejectABlankPolicyName()
    {
        // Arrange
        RouteEndpointBuilder endpointBuilder = CreateEndpointBuilder();

        // Act / Assert: a typo here would silently leave the whole group unlimited
        Should.Throw<ArgumentException>(() =>
            new TestConventionBuilder(endpointBuilder).RequireRateLimitingByDefault("  "));
    }

    [Fact]
    public void RequireRateLimitingByDefault_ShouldApplyTheDefaultThroughTheConventionBuilder()
    {
        // Arrange
        RouteEndpointBuilder endpointBuilder = CreateEndpointBuilder();
        TestConventionBuilder conventionBuilder = new(endpointBuilder);

        // Act
        conventionBuilder.RequireRateLimitingByDefault(DefaultPolicy);

        // Assert
        endpointBuilder.Metadata.OfType<EnableRateLimitingAttribute>()
            .Select(attribute => attribute.PolicyName)
            .ShouldBe([DefaultPolicy]);
    }

    private static RouteEndpointBuilder CreateEndpointBuilder()
    {
        return new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/Account/Login"),
            order: 0);
    }

    /// <summary>
    /// Runs a convention the moment it is added, so a test sees its effect without building a host.
    /// </summary>
    private sealed class TestConventionBuilder : IEndpointConventionBuilder
    {
        private readonly EndpointBuilder _endpointBuilder;

        public TestConventionBuilder(EndpointBuilder endpointBuilder)
        {
            _endpointBuilder = endpointBuilder;
        }

        public void Add(Action<EndpointBuilder> convention)
        {
            convention(_endpointBuilder);
        }

        public void Finally(Action<EndpointBuilder> finallyConvention)
        {
            finallyConvention(_endpointBuilder);
        }
    }
}
