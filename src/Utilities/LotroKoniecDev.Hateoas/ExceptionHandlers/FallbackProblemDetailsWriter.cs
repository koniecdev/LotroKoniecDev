using Microsoft.AspNetCore.Http;

namespace LotroKoniecDev.Hateoas.ExceptionHandlers;

/// <summary>
/// A last-resort <see cref="IProblemDetailsWriter"/> that writes RFC 7807
/// <c>application/problem+json</c> whatever the client's <c>Accept</c> header says.
/// ASP.NET Core's default writer only runs for clients that accept <c>application/json</c> or
/// <c>application/problem+json</c>. Without this one, a client asking for anything else (our vendor
/// type <c>application/vnd.dev-lotrokoniecdev.hateoas.json</c>, for example) would make
/// <see cref="IProblemDetailsService"/>.<c>WriteAsync</c> throw, and the error response would turn
/// into a 500 with no body.
/// <para>
/// It is registered after <c>AddProblemDetails()</c>, so the default writer keeps the Accept types it
/// supports and this one is tried last and always accepts.
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
