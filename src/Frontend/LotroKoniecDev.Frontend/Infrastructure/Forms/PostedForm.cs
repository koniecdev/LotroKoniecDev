using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace LotroKoniecDev.Frontend.Infrastructure.Forms;

/// <summary>
/// Reads which form a POST came from. Blazor writes the form's name into a hidden <c>_handler</c> field
/// of every named form, and reads that field to find the form's submit handler.
/// </summary>
internal static class PostedForm
{
    private const string HandlerField = "_handler";

    /// <summary>
    /// The name of the form this request posted, or <see langword="null"/> when it is not a form post.
    /// It reads only a form the server has already read, so it never reads the request body itself.
    /// Blazor has always read the form of a post it renders a page for.
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
