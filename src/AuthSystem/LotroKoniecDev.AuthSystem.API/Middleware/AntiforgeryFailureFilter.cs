using Microsoft.AspNetCore.Mvc.Core.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// Marks a failed antiforgery check, so the error page can say the form has expired (#879).
/// </summary>
/// <remarks>
/// The check answers with a bare 400 that later looks like any other, so the mark is set here. An
/// always-run filter, because the check refuses in an authorization filter, after which MVC runs only
/// this kind.
/// </remarks>
internal sealed class AntiforgeryFailureFilter : IAlwaysRunResultFilter
{
    private const string ItemsKey = "AntiforgeryValidationFailed";

    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is IAntiforgeryValidationFailedResult)
        {
            context.HttpContext.Items[ItemsKey] = true;
        }
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }

    internal static bool HasFailed(HttpContext httpContext) => httpContext.Items.ContainsKey(ItemsKey);
}
