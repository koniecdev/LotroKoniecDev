using Microsoft.AspNetCore.RateLimiting;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

/// <summary>
/// Makes a rate-limiting policy the default for a whole group of endpoints, so an endpoint that wants
/// no limit has to say so.
/// </summary>
internal static class RateLimitingEndpointConventions
{
    /// <summary>
    /// Attaches <paramref name="policyName"/> to every endpoint in the group that does not already
    /// carry a rate-limiting decision of its own.
    /// </summary>
    /// <remarks>
    /// A plain <c>RequireRateLimiting</c> cannot be used on a Razor Pages group, and the reason is easy
    /// to get backwards. <c>GetMetadata</c> returns the LAST matching item, and a group convention is
    /// appended after the metadata a PageModel attribute contributes. So on a page the group wins and
    /// silently replaces the page's own policy. On a minimal-API endpoint it is the other way round,
    /// because there the endpoint's own call runs after its group's. Same method, opposite outcome.
    /// Skipping pages that already decided keeps both halves working: a page can ask for a different
    /// budget with <c>[EnableRateLimiting]</c>, or for none with <c>[DisableRateLimiting]</c>.
    /// </remarks>
    internal static TBuilder RequireRateLimitingByDefault<TBuilder>(this TBuilder builder, string policyName)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        builder.Add(endpointBuilder => ApplyDefaultPolicy(endpointBuilder, policyName));
        return builder;
    }

    /// <summary>
    /// The body of the convention, separated so a test can drive it with a plain
    /// <see cref="EndpointBuilder"/> instead of a hand-written convention builder.
    /// </summary>
    internal static void ApplyDefaultPolicy(EndpointBuilder endpointBuilder, string policyName)
    {
        ArgumentNullException.ThrowIfNull(endpointBuilder);

        bool alreadyDecided = endpointBuilder.Metadata.Any(metadata =>
            metadata is EnableRateLimitingAttribute or DisableRateLimitingAttribute);

        if (alreadyDecided)
        {
            return;
        }

        endpointBuilder.Metadata.Add(new EnableRateLimitingAttribute(policyName));
    }
}
