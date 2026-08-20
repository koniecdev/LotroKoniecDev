using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Hateoas.ContentNegotiation;

/// <summary>
/// Reads the Accept header (RFC 7231 / RFC 9110) and picks between plain JSON and our link-carrying
/// JSON, using the quality values the client sent.
///
/// <para>Rules:</para>
/// <list type="bullet">
///   <item>No Accept header, or an empty one: plain JSON.</item>
///   <item>A wildcard (<c>*/*</c>, <c>application/*</c>): plain JSON, because links are opt-in.</item>
///   <item>Our vendor type listed with a quality at least as high as JSON: links.</item>
///   <item>Anything else: plain JSON.</item>
/// </list>
///
/// <para>
/// On a tie the vendor type wins, because the client asked for it by name and dropping the links
/// without saying so would surprise them.
/// </para>
///
/// <para>
/// This is a pure function over the Accept header, so it is a static class: no state, no
/// dependencies, and nothing worth substituting in a test. An interface with one implementation would
/// only exist so <see cref="HateoasNegotiatedResult{T}"/> could pull it out of DI.
/// </para>
///
/// <para>
/// When the Accept header matches neither type (say <c>text/html</c>), RFC 9110 §15.5.7 allows a
/// <c>406 Not Acceptable</c>. We return plain JSON instead, as most REST APIs do, because we have no
/// other representation to offer.
/// </para>
/// </summary>
internal static class HateoasContentNegotiator
{
    public static bool ShouldIncludeHateoas(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Headers.TryGetValue(HeaderNames.Accept, out StringValues acceptValues)
            || acceptValues.Count == 0)
        {
            return false;
        }

        if (!MediaTypeHeaderValue.TryParseList(acceptValues, out IList<MediaTypeHeaderValue>? parsed))
        {
            return false;
        }

        double hateoasQuality = -1;
        double jsonQuality = -1;

        foreach (MediaTypeHeaderValue mediaType in parsed)
        {
            if (!mediaType.MediaType.HasValue)
            {
                continue;
            }

            double quality = mediaType.Quality ?? 1.0;

            if (mediaType.MediaType.Equals(MediaTypes.HateoasJson, StringComparison.OrdinalIgnoreCase))
            {
                hateoasQuality = Math.Max(hateoasQuality, quality);
            }
            else if (mediaType.MediaType.Equals(MediaTypes.Json, StringComparison.OrdinalIgnoreCase))
            {
                jsonQuality = Math.Max(jsonQuality, quality);
            }
        }

        // The vendor type was not listed, or was refused with q=0.
        if (hateoasQuality <= 0)
        {
            return false;
        }

        // On a tie the vendor type wins: it is more specific and the client named it.
        return hateoasQuality >= jsonQuality;
    }
}
