using Microsoft.AspNetCore.Mvc.Core.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// Marks a request whose form failed the antiforgery check, so the browser's error page can say that the
/// form has expired (#879).
/// </summary>
/// <remarks>
/// The check answers with a bare 400. When the status-code pages write the answer later, that 400 looks
/// like any other one, so the mark is set here, where the check's own result is still visible. It is an
/// always-run filter because the check refuses the request in an authorization filter, and after that MVC
/// runs only this kind.
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
