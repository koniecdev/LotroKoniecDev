using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// The request leg of the e-mail change: what it refuses, and what it commits when it accepts
/// (spec 0013 / ADR-0048).
/// </summary>
public sealed class EmailChangeEndpointTests : EndpointsTestBase
{
    private const string Password = "TestPass1!";

    public EmailChangeEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task RequestEmailChange_ShouldReturnOkAndCommitOneOutboxRow_WhenTheRequestIsValid()
    {
        RegisterRequest registerRequest = await RegisterAsync();
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);
        string newEmail = UserFactory.GenerateUniqueEmail(Faker);

        HttpResponseMessage response = await RequestChangeAsync(accessToken, newEmail, Password);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Enqueue only adds the row to the unit of work; a handler that forgot to save would answer
        // 200 and send nothing at all.
        OutboxMessage? row = await OutboxAssertions.WaitForOutboxRowAsync(
            Factory,
            message => message.Type == nameof(EmailChangeRequested) && message.Payload.Contains(newEmail, StringComparison.Ordinal));

        row.ShouldNotBeNull();
        row.Payload.ShouldContain(registerRequest.Email);
        (await CountOutboxRowsAsync(nameof(EmailChangeRequested))).ShouldBe(1);
    }

    [Fact]
    public async Task RequestEmailChange_ShouldSendTheLinkToTheNewAddressAndWarnTheOldOne()
    {
        RegisterRequest registerRequest = await RegisterAsync();
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);
        string newEmail = UserFactory.GenerateUniqueEmail(Faker);

        await RequestChangeAsync(accessToken, newEmail, Password);
        await EmailChangeEmailSpy.WaitForVerificationCaptureAsync(newEmail);
        await EmailChangeEmailSpy.WaitForChangeRequestedWarningCaptureAsync(registerRequest.Email);

        EmailChangeEmailSpy.VerificationsTo(newEmail).ShouldHaveSingleItem();
        EmailChangeEmailSpy.WarningsTo(registerRequest.Email).ShouldHaveSingleItem().TargetAddress.ShouldBe(newEmail);
    }

    [Fact]
    public async Task RequestEmailChange_ShouldLeaveTheAccountOnTheOldAddress_UntilTheLinkIsUsed()
    {
        RegisterRequest registerRequest = await RegisterAsync();
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);
        string newEmail = UserFactory.GenerateUniqueEmail(Faker);

        await RequestChangeAsync(accessToken, newEmail, Password);
        await EmailChangeEmailSpy.WaitForVerificationCaptureAsync(newEmail);

        ApplicationUser user = await LoadUserAsync(registerRequest.Email);
        user.Email.ShouldBe(registerRequest.Email);

        // And the old address still logs in, which is the point of not moving anything yet.
        string tokenAfterRequest = await GetAccessTokenAsync(registerRequest.Email, Password);
        tokenAfterRequest.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RequestEmailChange_ShouldReturnProblemAndEnqueueNothing_WhenThePasswordIsWrong()
    {
        RegisterRequest registerRequest = await RegisterAsync();
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);

        HttpResponseMessage response = await RequestChangeAsync(
            accessToken, Faker.Internet.Email(), "TotallyWrong9!");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Auth.InvalidCurrentPassword");
        (await CountOutboxRowsAsync(nameof(EmailChangeRequested))).ShouldBe(0);
    }

    /// <summary>
    /// A taken address answers like a free one, and the requester's own inbox gets the same warning (#860).
    /// Only the owner of the taken address learns about the request, from a notice that carries no link.
    /// </summary>
    [Fact]
    public async Task RequestEmailChange_ShouldAnswerAsForAFreeAddressAndNotifyTheOwner_WhenTheAddressBelongsToAnotherAccount()
    {
        RegisterRequest registerRequest = await RegisterAsync();
        RegisterRequest otherUser = await RegisterAsync();
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);
        string freeEmail = UserFactory.GenerateUniqueEmail(Faker);

        using HttpResponseMessage takenResponse = await RequestChangeAsync(accessToken, otherUser.Email, Password);
        using HttpResponseMessage freeResponse = await RequestChangeAsync(accessToken, freeEmail, Password);

        takenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        takenResponse.StatusCode.ShouldBe(freeResponse.StatusCode);
        (await takenResponse.Content.ReadAsStringAsync()).ShouldBe(await freeResponse.Content.ReadAsStringAsync());

        // Each request sends its warning first and its last mail at the end, so after both waits no mail of
        // either request is still on its way.
        await EmailChangeEmailSpy.WaitForAddressTakenNoticeCaptureAsync(otherUser.Email);
        await EmailChangeEmailSpy.WaitForVerificationCaptureAsync(freeEmail);
        EmailChangeEmailSpy.AddressTakenNoticeCountTo(otherUser.Email).ShouldBe(1);
        EmailChangeEmailSpy.VerificationsTo(otherUser.Email).ShouldBeEmpty();
        EmailChangeEmailSpy.VerificationsTo(freeEmail).ShouldHaveSingleItem();
        EmailChangeEmailSpy.WarningsTo(registerRequest.Email).Count.ShouldBe(2);
    }

    [Fact]
    public async Task RequestEmailChange_ShouldReturnProblem_WhenTheAddressIsTheCallersOwn()
    {
        RegisterRequest registerRequest = await RegisterAsync();
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);

        HttpResponseMessage response = await RequestChangeAsync(
            accessToken, registerRequest.Email.ToUpperInvariant(), Password);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Auth.EmailChangeSameAddress");
    }

    [Fact]
    public async Task RequestEmailChange_ShouldReturnUnauthorized_WhenNoTokenIsPresented()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "auth/account/change-email")
        {
            Content = JsonContent.Create(new ChangeEmailRequest(Faker.Internet.Email(), Password))
        };

        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("spaces in@address.pl")]
    public async Task RequestEmailChange_ShouldReturnValidationProblem_WhenTheAddressIsMalformed(string newEmail)
    {
        RegisterRequest registerRequest = await RegisterAsync();
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);

        HttpResponseMessage response = await RequestChangeAsync(accessToken, newEmail, Password);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CountOutboxRowsAsync(nameof(EmailChangeRequested))).ShouldBe(0);
    }

    private async Task<RegisterRequest> RegisterAsync()
    {
        (RegisterRequest request, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        return request;
    }

    private async Task<HttpResponseMessage> RequestChangeAsync(
        string accessToken, string newEmail, string password)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "auth/account/change-email");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new ChangeEmailRequest(newEmail, password));

        return await ApiClient.Http.SendAsync(request);
    }

    private async Task<ApplicationUser> LoadUserAsync(string email)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        return await db.Set<ApplicationUser>()
            .AsNoTracking()
            .SingleAsync(user => user.NormalizedEmail == email.ToUpperInvariant());
    }

    private async Task<int> CountOutboxRowsAsync(string type)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        return await db.OutboxMessages.AsNoTracking().CountAsync(row => row.Type == type);
    }
}
