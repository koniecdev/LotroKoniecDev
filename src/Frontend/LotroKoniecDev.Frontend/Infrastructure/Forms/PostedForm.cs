using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace LotroKoniecDev.Frontend.Infrastructure.Forms;

/// <summary>
/// Blazor writes a named form's name into a hidden <c>_handler</c> field.
/// </summary>
internal static class PostedForm
{
    private const string HandlerField = "_handler";

    /// <summary>
    /// It reads only a form the server has already read, never the request body itself. Blazor always reads
    /// the form of a post it renders a page for.
    /// </summary>
    public static string? NameOf(HttpContext? httpContext)
    {
        if (httpContext is null || !HttpMethods.IsPost(httpContext.Request.Method))
        {
            return null;
        }

        return httpContext.Features.Get<IFormFeature>()?.Form is { } form
               && form.TryGetValue(HandlerField, out StringValues names)
               && names.Count == 1
            ? names[0]
            : null;
    }
}
