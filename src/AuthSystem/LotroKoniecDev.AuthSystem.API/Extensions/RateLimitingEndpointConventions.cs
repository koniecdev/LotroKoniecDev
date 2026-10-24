using Microsoft.AspNetCore.RateLimiting;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

/// <summary>
/// Makes a rate-limiting policy the default for a group, so an endpoint that wants no limit has to
/// say so.
/// </summary>
internal static class RateLimitingEndpointConventions
{
    /// <summary>
    /// Attaches <paramref name="policyName"/> to every endpoint that has no rate-limiting decision of its
    /// own.
    /// </summary>
    /// <remarks>
    /// Not a plain <c>RequireRateLimiting</c>: <c>GetMetadata</c> returns the last match, and a group
    /// convention is appended after a PageModel attribute, so the group would silently replace a page's
    /// own policy. On a minimal-API endpoint it is the other way round.
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
    /// Separate so a test can drive it with a plain <see cref="EndpointBuilder"/>.
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
