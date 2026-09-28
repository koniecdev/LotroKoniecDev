using LotroKoniecDev.AuthSystem.API.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Middleware;

/// <summary>
/// A failed antiforgery check and any other 400 look the same once the status-code pages run. Only this
/// mark tells them apart, so a wrong mark sends a user the wrong advice (#879).
/// </summary>
public sealed class AntiforgeryFailureFilterTests
{
    [Fact]
    public void OnResultExecuting_ShouldMarkTheRequest_WhenTheAntiforgeryCheckFailed()
    {
        // Arrange
        AntiforgeryFailureFilter filter = new();
        ResultExecutingContext context = BuildContext(new AntiforgeryValidationFailedResult());

        // Act
        filter.OnResultExecuting(context);

        // Assert
        AntiforgeryFailureFilter.HasFailed(context.HttpContext).ShouldBeTrue();
    }

    [Fact]
    public void OnResultExecuting_ShouldNotMarkTheRequest_WhenAnotherResultIsA400Too()
    {
        // Arrange: the same status for another reason must not tell the user the form has expired
        AntiforgeryFailureFilter filter = new();
        ResultExecutingContext context = BuildContext(new BadRequestResult());

        // Act
        filter.OnResultExecuting(context);

        // Assert
        AntiforgeryFailureFilter.HasFailed(context.HttpContext).ShouldBeFalse();
    }

    private static ResultExecutingContext BuildContext(IActionResult result)
    {
        ActionContext actionContext = new(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        return new ResultExecutingContext(actionContext, [], result, controller: new object());
    }
}
