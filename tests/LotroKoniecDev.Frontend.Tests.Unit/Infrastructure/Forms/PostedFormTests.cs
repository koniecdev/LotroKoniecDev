using System.Text;
using LotroKoniecDev.Frontend.Infrastructure.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Forms;

/// <summary>
/// Reads the posted form's name the way Blazor does, from the one <c>_handler</c> field (#1122).
/// </summary>
public sealed class PostedFormTests
{
    [Fact]
    public void NameOf_APostWithItsFormRead_ReturnsTheFormName()
    {
        HttpContext httpContext = Post(new Dictionary<string, StringValues> { ["_handler"] = "import-export" });

        PostedForm.NameOf(httpContext).ShouldBe("import-export");
    }

    [Fact]
    public void NameOf_WithNoHttpContext_ReturnsNull()
    {
        PostedForm.NameOf(null).ShouldBeNull();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public void NameOf_ARequestThatIsNotAPost_ReturnsNull(string method)
    {
        HttpContext httpContext = Post(new Dictionary<string, StringValues> { ["_handler"] = "import-export" });
        httpContext.Request.Method = method;

        PostedForm.NameOf(httpContext).ShouldBeNull();
    }

    [Fact]
    public void NameOf_APostWithNoFormName_ReturnsNull()
    {
        HttpContext httpContext = Post(new Dictionary<string, StringValues> { ["DraftField"] = "Cześć" });

        PostedForm.NameOf(httpContext).ShouldBeNull();
    }

    [Fact]
    public void NameOf_APostThatNamesTwoForms_ReturnsNull()
    {
        // Blazor refuses this post itself, so no page may pick one of the two names.
        HttpContext httpContext = Post(new Dictionary<string, StringValues>
        {
            ["_handler"] = new StringValues(["import-export", "register-version"])
        });

        PostedForm.NameOf(httpContext).ShouldBeNull();
    }

    [Fact]
    public void NameOf_APostWhoseFormWasNotReadYet_ReturnsNullWithoutReadingTheBody()
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("_handler=import-export"));

        PostedForm.NameOf(httpContext).ShouldBeNull();
        httpContext.Request.Body.Position.ShouldBe(0);
    }

    private static DefaultHttpContext Post(Dictionary<string, StringValues> fields)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Features.Set<IFormFeature>(new FormFeature(new FormCollection(fields)));
        return httpContext;
    }
}
