using System.Collections.Concurrent;
using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// LEGAL-06: no request may go to a Google font host from either origin, and the fonts we ship must really
/// load.
/// </summary>
public sealed class SelfHostedFontsTests : E2ETestBase
{
    public SelfHostedFontsTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Frontend_and_auth_pages_load_fonts_locally_and_send_nothing_to_google()
    {
        ConcurrentBag<string> requestedUrls = [];
        Page.Request += (_, request) => requestedUrls.Add(request.Url);

        string[] pages =
        [
            $"{Fixture.FrontendBaseUrl}/",
            $"{Fixture.FrontendBaseUrl}/regulamin",
            $"{Fixture.AuthBaseUrl}/Account/Login",
            $"{Fixture.AuthBaseUrl}/Account/Register",
            $"{Fixture.AuthBaseUrl}/Account/PrivacyPolicy"
        ];

        foreach (string url in pages)
        {
            await Page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        }

        // unicode-range faces download only once text uses them, so load both families explicitly, with
        // a Polish sample for the latin-ext subset.
        bool bothFamiliesLoaded = await Page.EvaluateAsync<bool>(
            """
            async () => {
                await document.fonts.load("700 16px Manrope", "Zażółć gęślą jaźń");
                await document.fonts.load("400 16px 'JetBrains Mono'", "Zażółć gęślą jaźń");
                return document.fonts.check("700 16px Manrope")
                    && document.fonts.check("400 16px 'JetBrains Mono'");
            }
            """);

        requestedUrls.ShouldNotBeEmpty();
        requestedUrls.Where(url => url.Contains("googleapis.com") || url.Contains("gstatic.com"))
            .ShouldBeEmpty();

        bothFamiliesLoaded.ShouldBeTrue();
        requestedUrls.Count(url => url.Contains("/fonts/manrope-", StringComparison.Ordinal)).ShouldBeGreaterThan(0);
        requestedUrls.Count(url => url.Contains("/fonts/jetbrains-mono-", StringComparison.Ordinal)).ShouldBeGreaterThan(0);
    }
}
