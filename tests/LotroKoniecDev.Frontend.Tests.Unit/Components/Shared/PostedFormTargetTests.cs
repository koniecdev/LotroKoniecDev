using AngleSharp.Dom;
using LotroKoniecDev.Frontend.Components.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Shared;

/// <summary>
/// The stand-in a page draws in its load-failure branch, so a post to a form the page cannot draw still
/// lands on the page (#1122). bUnit does not show the form's name, which lives in Blazor's named-event
/// frame, so the integration suite proves that a post finds it. These pin when it is drawn at all.
/// </summary>
public sealed class PostedFormTargetTests : BunitContext
{
    [Fact]
    public void Render_OnAPostToOneOfThePageForms_DrawsAnEmptyHiddenForm()
    {
        IRenderedComponent<PostedFormTarget> component = RenderTarget(Request(HttpMethods.Post, "import-export"));

        IElement form = component.Find("form");
        form.HasAttribute("hidden").ShouldBeTrue();
        form.GetAttribute("method").ShouldBe("post");
        form.Children.ShouldBeEmpty();
    }

    [Fact]
    public void Render_OnAPostToAnotherForm_DrawsNothing()
    {
        IRenderedComponent<PostedFormTarget> component = RenderTarget(Request(HttpMethods.Post, "no-such-form"));

        component.Markup.Trim().ShouldBeEmpty();
    }

    [Fact]
    public void Render_OnAGet_DrawsNothing()
    {
        IRenderedComponent<PostedFormTarget> component = RenderTarget(Request(HttpMethods.Get, "import-export"));

        component.Markup.Trim().ShouldBeEmpty();
    }

    private IRenderedComponent<PostedFormTarget> RenderTarget(HttpContext httpContext) =>
        Render<PostedFormTarget>(parameters => parameters
            .AddCascadingValue(httpContext)
            .Add(target => target.IsPageForm, static formName => formName == "import-export"));

    private static DefaultHttpContext Request(string method, string formName)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.Method = method;
        httpContext.Features.Set<IFormFeature>(new FormFeature(new FormCollection(
            new Dictionary<string, StringValues> { ["_handler"] = formName })));
        return httpContext;
    }
}
