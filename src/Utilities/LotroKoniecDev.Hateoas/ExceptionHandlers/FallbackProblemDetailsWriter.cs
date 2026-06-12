using Microsoft.AspNetCore.Http;

namespace LotroKoniecDev.Hateoas.ExceptionHandlers;

/// <summary>
/// Fallback <see cref="IProblemDetailsWriter"/> that writes RFC 7807
/// <c>application/problem+json</c> regardless of the client's <c>Accept</c>
/// header. ASP.NET Core's default writer only activates for clients that
/// accept <c>application/json</c> or <c>application/problem+json</c>, so
/// clients requesting any other representation (e.g. the HATEOAS vendor
/// media type <c>application/vnd.dev-lotrokoniecdev.hateoas.json</c>) would
/// otherwise cause <see cref="IProblemDetailsService"/>.<c>WriteAsync</c>
/// to throw — making the error response itself a 500 with no body.
/// <para>
/// Registered after <c>AddProblemDetails()</c>, so the default writer still
/// handles its supported Accept types; this writer is tried last and
/// unconditionally accepts the context.
/// </para>
/// </summary>
internal sealed class FallbackProblemDetailsWriter : IProblemDetailsWriter
{
    private const string ContentType = "application/problem+json";

    public bool CanWrite(ProblemDetailsContext context) => true;

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(
            context.ProblemDetails,
            options: null,
            contentType: ContentType,
            cancellationToken: context.HttpContext.RequestAborted));
    }
}
