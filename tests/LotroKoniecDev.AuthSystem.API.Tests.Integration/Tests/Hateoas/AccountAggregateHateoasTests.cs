using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.Hateoas.ContentNegotiation;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Hateoas;

/// <summary>
/// Checks that the account's links follow its current state and not the state it had when the access
/// token was issued.
/// In particular, <c>resend-email-confirmation</c> may only appear while the address is still
/// unconfirmed. Once it is confirmed, offering that action would point clients at something that leads
/// nowhere.
/// </summary>
public sealed class AccountAggregateHateoasTests : EndpointsTestBase
{
    private const string DataExportPath = "auth/account/data-export";
    private const string TestPassword = "TestPass1!";

    public AccountAggregateHateoasTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    [Fact]
    public async Task ExportAccountData_ShouldReturnBaseLinksOnly_WhenEmailIsConfirmed()
    {
        // Arrange - confirmed users see self, change-password, change-email, delete-account only
        (RegisterRequest registerRequest, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        // Act
        AccountDataExportResponse response = await RequestHateoasResponseAsync(accessToken);

        // Assert
        response.Links.Count.ShouldBe(4);
        response.Links.ShouldContain(l => l.Rel == Rels.Self && l.Method == "GET");
        response.Links.ShouldContain(l => l.Rel == Rels.ChangePassword && l.Method == "POST");
        response.Links.ShouldContain(l => l.Rel == Rels.ChangeEmail && l.Method == "POST");
        response.Links.ShouldContain(l => l.Rel == Rels.DeleteAccount && l.Method == "POST");
        response.Links.ShouldNotContain(
            l => l.Rel == Rels.ResendEmailConfirmation,
            "a confirmed user must not see the resend-email-confirmation transition");
    }

    [Fact]
    public async Task ExportAccountData_ShouldIncludeResendEmailConfirmationLink_WhenEmailIsUnconfirmed()
    {
        // Arrange - register & confirm (so we can issue a token, because OpenIddict's password
        // grant rejects unconfirmed accounts via SignInOptions.RequireConfirmedEmail), then
        // flip EmailConfirmed back to false directly in the DB to simulate a user whose
        // confirmation was revoked server-side. The JWT stays valid (stateless, not DB-checked),
        // so the handler sees EmailConfirmed=false and must advertise the resend transition.
        (RegisterRequest registerRequest, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        await SetEmailConfirmedAsync(registerRequest.Username, confirmed: false);

        // Act
        AccountDataExportResponse response = await RequestHateoasResponseAsync(accessToken);

        // Assert
        response.Links.Count.ShouldBe(5);
        response.Links.ShouldContain(l => l.Rel == Rels.Self && l.Method == "GET");
        response.Links.ShouldContain(l => l.Rel == Rels.ChangePassword && l.Method == "POST");
        response.Links.ShouldContain(l => l.Rel == Rels.ChangeEmail && l.Method == "POST");
        response.Links.ShouldContain(l => l.Rel == Rels.DeleteAccount && l.Method == "POST");
        response.Links.ShouldContain(l => l.Rel == Rels.ResendEmailConfirmation && l.Method == "POST");
    }

    [Fact]
    public async Task ExportAccountData_ShouldExposeOnlyCancelDeletionTransition_WhenDeletionIsScheduled()
    {
        // Arrange - schedule GDPR deletion; the self-contained JWT stays valid within
        // its lifetime, so the aggregate remains readable during the grace window and
        // must advertise cancel-deletion as the only meaningful transition.
        (RegisterRequest registerRequest, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        using HttpRequestMessage deleteRequest = new(HttpMethod.Post, "auth/account/delete");
        deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        deleteRequest.Content = JsonContent.Create(new DeleteAccountRequest(TestPassword));
        HttpResponseMessage deleteResponse = await ApiClient.Http.SendAsync(deleteRequest);
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Act
        AccountDataExportResponse response = await RequestHateoasResponseAsync(accessToken);

        // Assert
        response.AuthData.DeletionScheduledAt.ShouldNotBeNull();
        response.Links.Count.ShouldBe(2);
        response.Links.ShouldContain(l => l.Rel == Rels.Self && l.Method == "GET");
        response.Links.ShouldContain(l => l.Rel == Rels.CancelDeletion && l.Method == "POST");
        response.Links.ShouldNotContain(
            l => l.Rel == Rels.ChangePassword || l.Rel == Rels.ChangeEmail || l.Rel == Rels.DeleteAccount,
            "a deletion-scheduled account must not advertise dead transitions");
    }

    [Fact]
    public async Task ExportAccountData_SelfLink_ShouldPointAtDataExportEndpoint()
    {
        // Arrange
        (RegisterRequest registerRequest, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        // Act
        AccountDataExportResponse response = await RequestHateoasResponseAsync(accessToken);

        // Assert
        LinkDto selfLink = response.Links.First(l => l.Rel == Rels.Self);
        selfLink.Href.ShouldContain("/auth/account/data-export");
        selfLink.Method.ShouldBe("GET");
    }

    [Fact]
    public async Task ExportAccountData_AllLinks_ShouldHaveAbsoluteHrefs()
    {
        // Arrange - every HATEOAS href must be a usable absolute URI; relative URIs
        // force clients to guess the base address and break cross-origin consumers.
        (RegisterRequest registerRequest, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        // Act
        AccountDataExportResponse response = await RequestHateoasResponseAsync(accessToken);

        // Assert
        response.Links.ShouldNotBeEmpty();
        foreach (LinkDto link in response.Links)
        {
            link.Href.ShouldNotBeNullOrWhiteSpace();
            Uri.TryCreate(link.Href, UriKind.Absolute, out Uri? uri).ShouldBeTrue(
                $"HATEOAS href for rel='{link.Rel}' must be absolute; got '{link.Href}'");
            uri!.Scheme.ShouldMatch("https?");
        }
    }

    [Fact]
    public async Task ExportAccountData_ShouldOmitLinksProperty_WhenPlainJsonRequested()
    {
        // Arrange - content-negotiation guarantee: plain JSON response deserializes
        // into the same contract, but with Links as an empty collection (the 'links'
        // key itself is suppressed from the wire by HateoasJsonTypeInfoModifiers).
        (RegisterRequest registerRequest, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(DataExportPath, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.Json));

        // Act
        HttpResponseMessage httpResponse = await ApiClient.Http.SendAsync(request);
        string stringResponse = await httpResponse.EnsureSuccessWithDetailsAsync();
        AccountDataExportResponse response = JsonSerializer.Deserialize<AccountDataExportResponse>(
            stringResponse, ApiClient.JsonOptions)!;

        // Assert
        response.Links.Count.ShouldBe(0, "plain JSON response must not carry hypermedia links");
    }

    private async Task<AccountDataExportResponse> RequestHateoasResponseAsync(string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(DataExportPath, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.HateoasJson));

        HttpResponseMessage httpResponse = await ApiClient.Http.SendAsync(request);
        string stringResponse = await httpResponse.EnsureSuccessWithDetailsAsync();

        httpResponse.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.HateoasJson);

        return JsonSerializer.Deserialize<AccountDataExportResponse>(stringResponse, ApiClient.JsonOptions)!;
    }

    private async Task SetEmailConfirmedAsync(string username, bool confirmed)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        ApplicationUser user = await db.Users.FirstAsync(u => u.UserName == username);
        user.EmailConfirmed = confirmed;
        await db.SaveChangesAsync();
    }
}
