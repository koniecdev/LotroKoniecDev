using System.Text.RegularExpressions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// The SSR twin of the forgot-password API slice goes through the same outbox pipeline
/// (ADR-0038): posting the form commits an id-only outbox row instead of minting a token
/// in-request, and the anti-enumeration success panel shows either way.
/// </summary>
public sealed partial class ForgotPasswordPageTests : EndpointsTestBase
{
    public ForgotPasswordPageTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task ForgotPasswordPage_ShouldDeliverEmailThroughThePipeline_WhenUserExists()
    {
        // Arrange
        PasswordResetEmailSpy.Reset();

        (RegisterRequest request, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        // Act
        HttpResponseMessage response = await PostToForgotPasswordPageAsync(request.Email);
        await PasswordResetEmailSpy.WaitForCaptureAsync();

        // Assert: success panel plus a delivered e-mail whose token never touched the outbox row
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Jeśli konto istnieje");

        PasswordResetEmailSpy.CallCount.ShouldBe(1);
        PasswordResetEmailSpy.LastEmail.ShouldBe(request.Email);
        PasswordResetEmailSpy.LastResetToken.ShouldNotBeNullOrEmpty();

        OutboxMessage? outboxRow = await OutboxAssertions.WaitForOutboxRowAsync(
            Factory, row => row.Type == nameof(PasswordResetRequested));
        outboxRow.ShouldNotBeNull();
        PasswordResetRequested payload = JsonSerializer.Deserialize<PasswordResetRequested>(outboxRow.Payload)
            .ShouldNotBeNull();
        payload.IdentityUserId.ShouldBe(identityId.Value);
        outboxRow.Payload.ShouldNotContain(PasswordResetEmailSpy.LastResetToken!);
    }

    [Fact]
    public async Task ForgotPasswordPage_ShouldShowSuccessWithoutQueueingAnything_WhenUserDoesNotExist()
    {
        // Arrange
        PasswordResetEmailSpy.Reset();

        // Act
        HttpResponseMessage response = await PostToForgotPasswordPageAsync("nobody@example.com");

        // Assert: the anti-enumeration panel shows, but nothing was queued and nothing will send
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Jeśli konto istnieje");

        OutboxMessage? outboxRow = await OutboxAssertions.WaitForOutboxRowAsync(
            Factory, row => row.Type == nameof(PasswordResetRequested), TimeSpan.FromSeconds(1));
        outboxRow.ShouldBeNull();
        PasswordResetEmailSpy.CallCount.ShouldBe(0);
    }

    private async Task<HttpResponseMessage> PostToForgotPasswordPageAsync(string email)
    {
        HttpResponseMessage pageResponse = await ApiClient.Http.GetAsync(
            new Uri("/Account/ForgotPassword", UriKind.Relative));
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await pageResponse.Content.ReadAsStringAsync();
        string? antiForgeryToken = ExtractAntiForgeryToken(html);

        Dictionary<string, string> formFields = new()
        {
            ["Email"] = email
        };
        if (antiForgeryToken is not null)
        {
            formFields["__RequestVerificationToken"] = antiForgeryToken;
        }

        using FormUrlEncodedContent content = new(formFields);
        using HttpRequestMessage request = new(HttpMethod.Post, "/Account/ForgotPassword");
        request.Content = content;

        if (pageResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            foreach (string cookie in cookies)
            {
                request.Headers.Add("Cookie", cookie.Split(';')[0]);
            }
        }

        return await ApiClient.Http.SendAsync(request);
    }

    private static string? ExtractAntiForgeryToken(string html)
    {
        Match match = AntiForgeryTokenRegex().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();
}
