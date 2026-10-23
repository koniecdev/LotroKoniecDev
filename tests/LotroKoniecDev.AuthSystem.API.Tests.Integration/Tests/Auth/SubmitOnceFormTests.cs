using System.Text.RegularExpressions;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// A form behind a one-time link must be sent only once, or a double click shows "link dead" although the
/// first click did the work (#871). <c>submit-once.js</c> does it in the browser and the Frontend E2E suite
/// proves the double click; these tests hold the wiring.
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
    public async Task FormPage_ShouldMarkItsFormAndLoadTheSubmitOnceScript(string path)
    {
        // Act
        using HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(path, UriKind.Relative));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        FormTag().Matches(html).ShouldHaveSingleItem().Value.ShouldContain("data-submit-once");
        html.ShouldContain(ScriptTag);
    }

    /// <summary>
    /// A marked form restored from the browser cache asks for a fresh GET, so the server can answer for the
    /// used link (#941, ADR-0063). The login form leaves no used-link cookie, so a fresh GET would show it
    /// again.
    /// </summary>
    [Theory]
    [InlineData("/Account/ConfirmEmailChange?userId=00000000-0000-0000-0000-000000000001&email=c%40d.pl&token=z", true)]
    [InlineData("/Account/ResetPassword?email=a%40b.pl&token=z", true)]
    [InlineData("/Account/RevertEmailChange?userId=00000000-0000-0000-0000-000000000001&from=a%40b.pl&to=c%40d.pl&token=z", true)]
    [InlineData("/Account/CancelDeletion?email=a%40b.pl&token=z", true)]
    [InlineData("/Account/Login", false)]
    public async Task FormPage_ShouldAskForAFreshPageWhenRestoredFromTheCache_OnlyBehindAOneTimeLink(
        string path, bool expectsTheMark)
    {
        // Act
        using HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(path, UriKind.Relative));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        FormTag().Matches(html).ShouldHaveSingleItem().Value.Contains("data-recheck-when-restored", StringComparison.Ordinal)
            .ShouldBe(expectsTheMark);
    }

    [Fact]
    public async Task SubmitOnceScript_ShouldBeServedFromThisOrigin()
    {
        // Act
        using HttpResponseMessage script = await ApiClient.Http.GetAsync(new Uri("/submit-once.js", UriKind.Relative));

        // Assert
        script.StatusCode.ShouldBe(HttpStatusCode.OK);
        script.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("text/javascript");
        string body = await script.Content.ReadAsStringAsync();
        body.ShouldContain("form[data-submit-once]");
    }

    [GeneratedRegex("<form[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex FormTag();
}
