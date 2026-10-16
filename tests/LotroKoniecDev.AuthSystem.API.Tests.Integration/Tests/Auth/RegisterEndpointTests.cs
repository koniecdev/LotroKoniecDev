using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed class RegisterEndpointTests : EndpointsTestBase
{
    public RegisterEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task Register_ShouldReturnAcceptedWithNoBodyAndSendTheConfirmation_WhenValidDataIsProvided()
    {
        // Arrange
        AccountConfirmationEmailSpy.Reset();
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.Content.ReadAsStringAsync()).ShouldBeEmpty();
        await AccountConfirmationEmailSpy.WaitForCaptureAsync();
        AccountConfirmationEmailSpy.LastEmail.ShouldBe(request.Email);
    }

    [Fact]
    public async Task Register_ShouldAnswerAsForAFreeAddressAndNotifyTheOwner_WhenEmailAlreadyExists()
    {
        // Arrange
        (RegisterRequest existingRequest, IdentityId existingId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        AccountConfirmationEmailSpy.Reset();

        RegisterRequest freeRequest = UserFactory.GenerateRandomRegisterRequest(Faker);
        RegisterRequest duplicateRequest = UserFactory.GenerateRandomRegisterRequest(Faker) with
        {
            Email = existingRequest.Email
        };

        // Act
        using HttpResponseMessage takenResponse = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), duplicateRequest);
        using HttpResponseMessage freeResponse = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), freeRequest);

        // Assert
        takenResponse.StatusCode.ShouldBe(freeResponse.StatusCode);
        (await takenResponse.Content.ReadAsStringAsync()).ShouldBe(await freeResponse.Content.ReadAsStringAsync());
        takenResponse.Content.Headers.ContentType.ShouldBe(freeResponse.Content.Headers.ContentType);

        await AccountConfirmationEmailSpy.WaitForRegistrationAttemptedCaptureAsync();
        AccountConfirmationEmailSpy.LastRegistrationAttemptedRecipient.ShouldBe(existingRequest.Email);
        AccountConfirmationEmailSpy.RegistrationAttemptedCallCount.ShouldBe(1);

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        (await db.Users.AnyAsync(user => user.UserName == duplicateRequest.Username)).ShouldBeFalse();
        (await db.Users.SingleAsync(user => user.NormalizedEmail == existingRequest.Email.ToUpperInvariant()))
            .Id.ShouldBe(existingId.Value);
    }

    [Fact]
    public async Task Register_ShouldQueueNoSecondConfirmation_WhenTheAddressIsAlreadyRegistered()
    {
        // Arrange: registration mails an address the caller typed, and one address registers once
        // (ADR-0055). The account stays unconfirmed, because that is the state a flood would keep
        // registering against. The budget per inbox on top of this is in MailboxBudgetTests (ADR-0057).
        (RegisterRequest existingRequest, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        int rowsAfterFirst = await CountConfirmationRowsAsync();

        string[] repeats = [existingRequest.Email, existingRequest.Email.ToUpperInvariant()];

        // Act
        foreach (string email in repeats)
        {
            RegisterRequest duplicateRequest = UserFactory.GenerateRandomRegisterRequest(Faker) with { Email = email };
            using HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
                new Uri("auth/register", UriKind.Relative), duplicateRequest);
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        // Assert
        rowsAfterFirst.ShouldBe(1);
        (await CountConfirmationRowsAsync()).ShouldBe(rowsAfterFirst);
    }

    [Fact]
    public async Task Register_ShouldNotifyTheOwnerAndCreateNoAccount_WhenEmailDiffersOnlyByCase()
    {
        // Arrange
        (RegisterRequest existingRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        AccountConfirmationEmailSpy.Reset();

        string caseVariantEmail = existingRequest.Email.ToUpperInvariant();
        caseVariantEmail.ShouldNotBe(existingRequest.Email);

        RegisterRequest duplicateRequest = UserFactory.GenerateRandomRegisterRequest(Faker) with
        {
            Email = caseVariantEmail
        };

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), duplicateRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await AccountConfirmationEmailSpy.WaitForRegistrationAttemptedCaptureAsync();
        AccountConfirmationEmailSpy.LastRegistrationAttemptedRecipient.ShouldBe(existingRequest.Email);

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        (await db.Users.AnyAsync(user => user.UserName == duplicateRequest.Username)).ShouldBeFalse();
    }

    /// <summary>
    /// The username is checked before the address, so a known taken username tells nothing about the
    /// address it is paired with: both answer "username taken" (#860).
    /// </summary>
    [Fact]
    public async Task Register_ShouldAnswerUsernameTakenWhetherOrNotTheAddressIsTaken_WhenUsernameAlreadyExists()
    {
        // Arrange
        (RegisterRequest existingRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        (RegisterRequest otherAccount, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        AccountConfirmationEmailSpy.Reset();

        RegisterRequest withFreeAddress = UserFactory.GenerateRandomRegisterRequest(Faker) with
        {
            Username = existingRequest.Username
        };
        RegisterRequest withTakenAddress = withFreeAddress with { Email = otherAccount.Email };

        // Act
        using HttpResponseMessage freeAddressResponse = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), withFreeAddress);
        using HttpResponseMessage takenAddressResponse = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), withTakenAddress);

        // Assert
        freeAddressResponse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        takenAddressResponse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await freeAddressResponse.Content.ReadAsStringAsync()).ShouldContain("Auth.UserAlreadyExistsByUsername");
        (await takenAddressResponse.Content.ReadAsStringAsync()).ShouldContain("Auth.UserAlreadyExistsByUsername");
        (await CountNoticeRowsAsync()).ShouldBe(0);
    }

    /// <summary>
    /// The breach check runs before the address is looked up, so a leaked password gets the same refusal
    /// for a taken address as for a free one (#860).
    /// </summary>
    [Fact]
    public async Task Register_ShouldRefuseTheLeakedPassword_WhenTheAddressAlreadyHasAnAccount()
    {
        // Arrange
        (RegisterRequest existingRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        AccountConfirmationEmailSpy.Reset();
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker) with
        {
            Email = existingRequest.Email
        };
        Factory.PwnedPasswords.MarkBreached(request.Password);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("errorCode").GetString().ShouldBe("Auth.PasswordFoundInBreaches");
        (await CountNoticeRowsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Register_ShouldReturnUnprocessableEntity_WhenUsernameAlreadyExists()
    {
        // Arrange
        (RegisterRequest existingRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        RegisterRequest duplicateRequest = new(
            existingRequest.Username,
            Faker.Internet.Email(),
            "TestPass1!",
            AcceptedPrivacyPolicy: true,
            AcceptedTermsOfService: true);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), duplicateRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Register_ShouldReturnUnprocessableEntity_WhenUsernameDiffersOnlyByCase()
    {
        // Arrange: Identity normalization makes the handle uniqueness case-insensitive (ADR-0022)
        (RegisterRequest existingRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        string caseVariantUsername = existingRequest.Username.ToUpperInvariant();
        caseVariantUsername.ShouldNotBe(existingRequest.Username);

        RegisterRequest duplicateRequest = new(
            caseVariantUsername,
            Faker.Internet.Email(),
            "TestPass1!",
            AcceptedPrivacyPolicy: true,
            AcceptedTermsOfService: true);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), duplicateRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        string content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("Auth.UserAlreadyExistsByUsername");
    }

    [Theory]
    [InlineData("kasia 92")]
    [InlineData("kasia.92")]
    [InlineData("kasia_92")]
    [InlineData("kasia@92")]
    [InlineData("kaśka92")]
    [InlineData("kasia-92")]
    [InlineData("kasia92\n")] // .NET's $ matches before a trailing \n — \A…\z anchoring must reject this
    [InlineData(" kasia92")]
    public async Task Register_ShouldReturnBadRequestWithCharsetMessage_WhenUsernameHasIllegalCharacters(
        string username)
    {
        // Arrange
        RegisterRequest request = new(
            username,
            Faker.Internet.Email(),
            "TestPass1!",
            AcceptedPrivacyPolicy: true,
            AcceptedTermsOfService: true);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), request);

        // Assert: the explicit validator message, never Identity's raw English error surfacing late
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        string content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("Username may contain only letters and digits, without spaces.");
        content.ShouldContain("RegisterUser.Validation");
        content.ShouldNotContain("Auth.RegistrationFailed");
    }

    [Theory]
    [InlineData("kasia92")]
    [InlineData("KASIA92")]
    public async Task Register_ShouldReturnAccepted_WhenUsernameIsAlphanumeric(string username)
    {
        // Arrange
        RegisterRequest request = new(
            username,
            Faker.Internet.Email(),
            "TestPass1!",
            AcceptedPrivacyPolicy: true,
            AcceptedTermsOfService: true);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), request);

        // Assert
        await response.EnsureSuccessWithDetailsAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Register_ShouldReturnBadRequest_WhenPrivacyPolicyNotAccepted()
    {
        // Arrange
        RegisterRequest request = new(
            Faker.Random.AlphaNumeric(16),
            Faker.Internet.Email(),
            "TestPass1!",
            AcceptedPrivacyPolicy: false,
            AcceptedTermsOfService: true);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_ShouldReturnBadRequest_WhenTermsOfServiceNotAccepted()
    {
        // Arrange
        RegisterRequest request = new(
            Faker.Random.AlphaNumeric(16),
            Faker.Internet.Email(),
            "TestPass1!",
            AcceptedPrivacyPolicy: true,
            AcceptedTermsOfService: false);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        string content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("You must accept the terms of service to register.");
    }

    /// <summary>
    /// The account runs on the contract, so registration neither asks for a data-processing consent nor
    /// stores one, whatever an older client still sends for it (#1073).
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Register_ShouldStoreNoDataProcessingConsent_WhenTheClientSendsAnyValueOrNone(
        bool? acceptedDataProcessingConsent)
    {
        // Arrange
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);
        JsonObject body = JsonSerializer.SerializeToNode(request, ApiClient.JsonOptions)!.AsObject();
        if (acceptedDataProcessingConsent is { } sentValue)
        {
            body["acceptedDataProcessingConsent"] = sentValue;
        }

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), body);

        // Assert
        await response.EnsureSuccessWithDetailsAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        ApplicationUser user = await db.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.UserName == request.Username);
        user.DataProcessingConsentGiven.ShouldBeFalse();
        user.DataProcessingConsentDate.ShouldBeNull();
    }

    [Fact]
    public async Task Register_ShouldReturnBadRequest_WhenPasswordIsTooWeak()
    {
        // Arrange
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker, password: "weak");

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The notice is committed before the answer is sent, so a count right after the answer is final.
    /// </summary>
    private async Task<int> CountNoticeRowsAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        return await db.OutboxMessages
            .AsNoTracking()
            .CountAsync(row => row.Type == nameof(RegistrationAttemptedOnExistingAccount));
    }

    private async Task<int> CountConfirmationRowsAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        return await db.OutboxMessages
            .AsNoTracking()
            .CountAsync(row => row.Type == nameof(EmailConfirmationRequested));
    }

    // The former Register_ShouldAutoConfirmEmail_WhenEmailSendingFails test died with the
    // synchronous send: registration no longer touches SMTP, so there is no in-request failure
    // to fall back from. A failed delivery now stays queued for redelivery, and the user-facing
    // recovery is the resend-confirmation endpoint.

    [Fact]
    public async Task Register_ShouldRefuseThePasswordAndCreateNoAccount_WhenItAppearsInDataBreaches()
    {
        // Arrange
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);
        Factory.PwnedPasswords.MarkBreached(request.Password);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("errorCode").GetString().ShouldBe("Auth.PasswordFoundInBreaches");

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        (await db.Users.AnyAsync(user => user.UserName == request.Username)).ShouldBeFalse();
    }

    /// <summary>
    /// The failure policy of ADR-0066: a breach service that is down lets the password through, so it does
    /// not take registration down with it. The password here is one the service would refuse.
    /// </summary>
    [Fact]
    public async Task Register_ShouldCreateTheAccount_WhenThePwnedPasswordsServiceIsDown()
    {
        // Arrange
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);
        Factory.PwnedPasswords.MarkBreached(request.Password);
        Factory.PwnedPasswords.TakeDown();

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), request);

        // Assert
        await response.EnsureSuccessWithDetailsAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }
}
