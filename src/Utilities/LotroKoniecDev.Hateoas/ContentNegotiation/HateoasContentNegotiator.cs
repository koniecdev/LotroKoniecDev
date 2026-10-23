using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Hateoas.ContentNegotiation;

/// <summary>
/// Links are opt-in, so a missing Accept header or a wildcard (<c>*/*</c>, <c>application/*</c>) gets
/// plain JSON. The vendor type wins when the client lists it with a quality at least as high as plain
/// JSON's: the client named it, and dropping the links without saying so would surprise them.
/// <para>
/// An Accept header that matches neither type (say <c>text/html</c>) also gets plain JSON, not the
/// <c>406</c> that RFC 9110 §15.5.7 allows: we have no other representation to offer.
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

        return hateoasQuality >= jsonQuality;
    }
}
