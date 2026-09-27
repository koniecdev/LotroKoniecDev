using System.Text.RegularExpressions;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// A form behind a one-time link must be sent only once. Otherwise a double click shows the answer to the
/// second click, "link dead", although the first click did the work (#871). The browser side lives in
/// <c>submit-once.js</c>, and the Frontend E2E suite proves the double click itself. These tests hold the
/// wiring: each page marks its form and loads that file from its own origin.
/// </summary>
public sealed partial class SubmitOnceFormTests : EndpointsTestBase
{
    private const string ScriptTag = "<script src=\"/submit-once.js\"></script>";

    public SubmitOnceFormTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    /// <summary>
    /// Each link parses, so the page renders its form. The token is never checked on a GET.
    /// </summary>
    [Theory]
    [InlineData("/Account/ConfirmEmailChange?userId=00000000-0000-0000-0000-000000000001&email=c%40d.pl&token=z")]
    [InlineData("/Account/RevertEmailChange?userId=00000000-0000-0000-0000-000000000001&from=a%40b.pl&to=c%40d.pl&token=z")]
    [InlineData("/Account/CancelDeletion?email=a%40b.pl&token=z")]
    [InlineData("/Account/ResetPassword?email=a%40b.pl&token=z")]
    [InlineData("/Account/Login")]
    public async Task FormPage_ShouldSendItsFormOnlyOnce(string path)
    {
        // Act
        using HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(path, UriKind.Relative));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        FormTag().Matches(html).ShouldHaveSingleItem().Value.ShouldContain("data-submit-once");
        html.ShouldContain(ScriptTag);
    }

    [Fact]
    public async Task SubmitOnceScript_ShouldBeServedFromThisOrigin()
    {
        // Act
        using HttpResponseMessage script = await ApiClient.Http.GetAsync(new Uri("/submit-once.js", UriKind.Relative));

        // Assert
        script.StatusCode.ShouldBe(HttpStatusCode.OK);
        script.Content.Headers.ContentType?.MediaType.ShouldBe("text/javascript");
        string body = await script.Content.ReadAsStringAsync();
        body.ShouldContain("form[data-submit-once]");
    }

    [GeneratedRegex("<form[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex FormTag();
}
