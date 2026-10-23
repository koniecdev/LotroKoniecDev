using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.Hateoas.ExceptionHandlers;

/// <summary>
/// ASP.NET Core's default writer only serves clients that accept <c>application/json</c> or
/// <c>application/problem+json</c>. For any other Accept value, such as our vendor type,
/// <see cref="IProblemDetailsService"/>.<c>WriteAsync</c> would throw and the error would become a 500
/// with no body. This writer is registered after <c>AddProblemDetails()</c>, so it is tried last and
/// always accepts.
/// <para>
/// It runs <see cref="ProblemDetailsOptions.CustomizeProblemDetails"/> like the default writer does, so
/// what an app adds there reaches every client (#912).
/// </para>
/// </summary>
internal sealed class FallbackProblemDetailsWriter : IProblemDetailsWriter
{
    private const string ContentType = "application/problem+json";

    private readonly ProblemDetailsOptions _options;

    public FallbackProblemDetailsWriter(IOptions<ProblemDetailsOptions> options)
    {
        _options = options.Value;
    }

    public bool CanWrite(ProblemDetailsContext context) => true;

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _options.CustomizeProblemDetails?.Invoke(context);

        return new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(
            context.ProblemDetails,
            options: null,
            contentType: ContentType,
            cancellationToken: context.HttpContext.RequestAborted));
    }
}
