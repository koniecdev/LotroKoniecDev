using System.Data.Common;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Identity;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// An e-mail change whose save fails while it is confirmed or undone. Only a real duplicate on an e-mail
/// index may come back as a taken address. Any other error is an outage: a 500, and nothing changes
/// (#864).
/// </summary>
public sealed partial class EmailChangeSaveFailureTests : EndpointsTestBase
{
    private const string Password = "TestPass1!";

    public EmailChangeSaveFailureTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    /// <summary>
    /// The username index belongs to this table, but an e-mail change never writes a username, so a
    /// duplicate there says nothing about the address.
    /// </summary>
    [Theory]
    [InlineData(PostgresErrorCodes.NotNullViolation, null)]
    [InlineData(PostgresErrorCodes.UniqueViolation, "UserNameIndex")]
    [InlineData(PostgresErrorCodes.UniqueViolation, "IX_NotAUsersIndex")]
    public async Task ConfirmPage_Post_ShouldReturnInternalServerErrorAndKeepTheOldAddress_WhenTheSaveFailsForAnotherReason(
        string sqlState,
        string? constraintName)
    {
        // Arrange
        (RegisterRequest user, string newEmail, string token) = await RequestChangeAsync();
        Guid userId = await UserIdOfAsync(user.Email);
        Factory.DbCommandFailures.FailNext(IsUpdateOfUsers, () => CreatePermanentFailure(sqlState, constraintName));

        // Act
        HttpResponseMessage response = await PostToPageAsync(
            ApiClient.Http,
            "/Account/ConfirmEmailChange",
            ConfirmUrl(userId, newEmail, token),
            ConfirmForm(userId, newEmail, token));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);
        (await LoadUserByIdAsync(userId)).Email.ShouldBe(user.Email);
    }

    [Theory]
    [InlineData(PostgresErrorCodes.NotNullViolation, null)]
    [InlineData(PostgresErrorCodes.UniqueViolation, "UserNameIndex")]
    [InlineData(PostgresErrorCodes.UniqueViolation, "IX_NotAUsersIndex")]
    public async Task RevertPage_Post_ShouldReturnInternalServerErrorAndKeepThePassword_WhenTheSaveFailsForAnotherReason(
        string sqlState,
        string? constraintName)
    {
        // Arrange
        (RegisterRequest user, string newEmail, Guid userId) = await CompleteChangeAsync();
        string revertToken = EmailChangeEmailSpy.LastRevertToken!;
        Factory.DbCommandFailures.FailNext(IsUpdateOfUsers, () => CreatePermanentFailure(sqlState, constraintName));

        // Act
        HttpResponseMessage response = await PostToPageAsync(
            ApiClient.Http,
            "/Account/RevertEmailChange",
            RevertUrl(userId, user.Email, newEmail, revertToken),
            RevertForm(userId, user.Email, newEmail, revertToken));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);

        ApplicationUser untouched = await LoadUserByIdAsync(userId);
        untouched.Email.ShouldBe(newEmail);
        untouched.PasswordHash.ShouldNotBeNull();
    }

    /// <summary>
    /// Neither handler opens a transaction of its own, so the save replays a passing error by itself and
    /// the catch never sees it.
    /// </summary>
    [Fact]
    public async Task ConfirmPage_Post_ShouldMoveTheAddress_WhenATransientFailureHitsTheSave()
    {
        // Arrange
        (RegisterRequest user, string newEmail, string token) = await RequestChangeAsync();
        Guid userId = await UserIdOfAsync(user.Email);
        Factory.DbCommandFailures.FailNext(IsUpdateOfUsers, CreateTransientFailure);

        // Act
        HttpResponseMessage response = await PostToPageAsync(
            ApiClient.Http,
            "/Account/ConfirmEmailChange",
            ConfirmUrl(userId, newEmail, token),
            ConfirmForm(userId, newEmail, token));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);
        (await LoadUserByIdAsync(userId)).Email.ShouldBe(newEmail);
    }

    [Fact]
    public async Task RevertPage_Post_ShouldRestoreTheAddress_WhenATransientFailureHitsTheSave()
    {
        // Arrange
        (RegisterRequest user, string newEmail, Guid userId) = await CompleteChangeAsync();
        string revertToken = EmailChangeEmailSpy.LastRevertToken!;
        Factory.DbCommandFailures.FailNext(IsUpdateOfUsers, CreateTransientFailure);

        // Act
        HttpResponseMessage response = await PostToPageAsync(
            ApiClient.Http,
            "/Account/RevertEmailChange",
            RevertUrl(userId, user.Email, newEmail, revertToken),
            RevertForm(userId, user.Email, newEmail, revertToken));

        // Assert: the test client follows redirects, so landing on the reset page is what proves success
        response.RequestMessage!.RequestUri!.ToString().ShouldContain("/Account/ResetPassword");
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);
        (await LoadUserByIdAsync(userId)).Email.ShouldBe(user.Email);
    }

    /// <summary>
    /// The confirm page gives one answer for every refusal, so here a lost race shows as a 200 with that
    /// answer, where an outage is a 500.
    /// </summary>
    [Fact]
    public async Task ConfirmPage_Post_ShouldRefuseAndKeepTheOldAddress_WhenAnotherAccountTakesTheAddressJustBeforeTheSave()
    {
        // Arrange: the competitor is created through the suite's main host, whose contexts do not carry
        // this interceptor, so it commits while our update waits
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        Guid userId = await UserIdOfAsync(user.Email);
        string newEmail = Faker.Internet.Email();

        CompetitorTakesTheAddressFirstInterceptor interceptor = new(newEmail, () => SeedUserOnAsync(newEmail));
        await using WebApplicationFactory<Program> host = CreateHostWith(interceptor);
        using HttpClient client = host.CreateClient();

        string token = await CreateTokenAsync(
            host.Services,
            userId,
            EmailChangeTokenProvider.ProviderName,
            EmailChangeTokenProvider.PurposeFor(newEmail));

        // Act
        HttpResponseMessage response = await PostToPageAsync(
            client,
            "/Account/ConfirmEmailChange",
            ConfirmUrl(userId, newEmail, token),
            ConfirmForm(userId, newEmail, token));

        // Assert
        interceptor.CompetitorCommitted.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("nieprawidłowy lub wygasł");
        (await LoadUserByIdAsync(userId)).Email.ShouldBe(user.Email);
    }

    [Fact]
    public async Task RevertPage_Post_ShouldRefuseAndKeepThePassword_WhenAnotherAccountTakesThePreviousAddressJustBeforeTheSave()
    {
        // Arrange
        (RegisterRequest user, string newEmail, Guid userId) = await CompleteChangeAsync();

        CompetitorTakesTheAddressFirstInterceptor interceptor = new(user.Email, () => SeedUserOnAsync(user.Email));
        await using WebApplicationFactory<Program> host = CreateHostWith(interceptor);
        using HttpClient client = host.CreateClient();

        string revertToken = await CreateTokenAsync(
            host.Services,
            userId,
            EmailChangeRevertTokenProvider.ProviderName,
            EmailChangeRevertTokenProvider.PurposeFor(user.Email, newEmail));

        // Act
        HttpResponseMessage response = await PostToPageAsync(
            client,
            "/Account/RevertEmailChange",
            RevertUrl(userId, user.Email, newEmail, revertToken),
            RevertForm(userId, user.Email, newEmail, revertToken));

        // Assert
        interceptor.CompetitorCommitted.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Poprzedni adres należy już do innego konta");

        ApplicationUser untouched = await LoadUserByIdAsync(userId);
        untouched.Email.ShouldBe(newEmail);
        untouched.PasswordHash.ShouldNotBeNull();
    }

    private WebApplicationFactory<Program> CreateHostWith(DbCommandInterceptor interceptor) =>
        Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // A second relay on this database could take a row another test waits for.
                AuthSystemApiFactory.RemoveHostedService<OutboxRelay>(services);
                services.ConfigureDbContext<AuthDbContext>(options => options.AddInterceptors(interceptor));
            }));

    /// <summary>
    /// Both tokens are sealed with data protection, and nothing makes two test hosts share a key ring, so
    /// the token comes from the host that will check it.
    /// </summary>
    private static async Task<string> CreateTokenAsync(
        IServiceProvider services, Guid userId, string provider, string purpose)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser? user = await userManager.FindByIdAsync(userId.ToString());
        user.ShouldNotBeNull();

        return await userManager.GenerateUserTokenAsync(user, provider, purpose);
    }

    /// <summary>
    /// Creates the account through the store, which is the only way onto an address the reservation of
    /// #684 holds.
    /// </summary>
    private async Task SeedUserOnAsync(string email)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser squatter = new()
        {
            UserName = Faker.Random.AlphaNumeric(16),
            Email = email
        };

        (await userManager.CreateAsync(squatter, Password)).Succeeded.ShouldBeTrue();
    }

    private async Task<(RegisterRequest User, string NewEmail, string Token)> RequestChangeAsync()
    {
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);

        string accessToken = await GetAccessTokenAsync(user.Email, Password);
        string newEmail = Faker.Internet.Email();

        using HttpRequestMessage request = new(HttpMethod.Post, "auth/account/change-email");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new ChangeEmailRequest(newEmail, Password));
        (await ApiClient.Http.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await EmailChangeEmailSpy.WaitForVerificationCaptureAsync();

        return (user, newEmail, EmailChangeEmailSpy.LastVerificationToken!);
    }

    private async Task<(RegisterRequest User, string NewEmail, Guid UserId)> CompleteChangeAsync()
    {
        (RegisterRequest user, string newEmail, string token) = await RequestChangeAsync();
        Guid userId = await UserIdOfAsync(user.Email);

        HttpResponseMessage response = await PostToPageAsync(
            ApiClient.Http,
            "/Account/ConfirmEmailChange",
            ConfirmUrl(userId, newEmail, token),
            ConfirmForm(userId, newEmail, token));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await EmailChangeEmailSpy.WaitForRevertOfferCaptureAsync();

        return (user, newEmail, userId);
    }

    private static bool IsUpdateOfUsers(DbCommand command) =>
        command.CommandText.Contains($"UPDATE {DatabaseSchemas.Auth}.\"Users\"", StringComparison.Ordinal);

    private static PostgresException CreatePermanentFailure(string sqlState, string? constraintName) =>
        new("simulated permanent failure", "ERROR", "ERROR", sqlState, constraintName: constraintName);

    private static NpgsqlException CreateTransientFailure() =>
        new("The operation has timed out", new TimeoutException());

    private static Dictionary<string, string> ConfirmForm(Guid userId, string newEmail, string token) =>
        new()
        {
            ["UserId"] = userId.ToString(),
            ["Email"] = newEmail,
            ["Token"] = token
        };

    private static Dictionary<string, string> RevertForm(Guid userId, string from, string to, string token) =>
        new()
        {
            ["UserId"] = userId.ToString(),
            ["From"] = from,
            ["To"] = to,
            ["Token"] = token
        };

    private static string ConfirmUrl(Guid userId, string newEmail, string token) =>
        $"/Account/ConfirmEmailChange?userId={userId}&email={Uri.EscapeDataString(newEmail)}"
        + $"&token={Uri.EscapeDataString(token)}";

    private static string RevertUrl(Guid userId, string from, string to, string token) =>
        $"/Account/RevertEmailChange?userId={userId}&from={Uri.EscapeDataString(from)}"
        + $"&to={Uri.EscapeDataString(to)}&token={Uri.EscapeDataString(token)}";

    private async Task<Guid> UserIdOfAsync(string email)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        return await db.Set<ApplicationUser>()
            .AsNoTracking()
            .Where(user => user.NormalizedEmail == email.ToUpperInvariant())
            .Select(user => user.Id)
            .SingleAsync();
    }

    private async Task<ApplicationUser> LoadUserByIdAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        return await db.Set<ApplicationUser>().AsNoTracking().SingleAsync(user => user.Id == userId);
    }

    private static async Task<HttpResponseMessage> PostToPageAsync(
        HttpClient client, string pagePath, string getUrl, Dictionary<string, string> formFields)
    {
        HttpResponseMessage pageResponse = await client.GetAsync(new Uri(getUrl, UriKind.Relative));
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await pageResponse.Content.ReadAsStringAsync();
        Match match = AntiForgeryTokenRegex().Match(html);
        if (match.Success)
        {
            formFields["__RequestVerificationToken"] = match.Groups[1].Value;
        }

        using FormUrlEncodedContent content = new(formFields);
        using HttpRequestMessage request = new(HttpMethod.Post, pagePath) { Content = content };

        if (pageResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            foreach (string cookie in cookies)
            {
                request.Headers.Add("Cookie", cookie.Split(';')[0]);
            }
        }

        return await client.SendAsync(request);
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();

    /// <summary>
    /// Holds the account's update until another account has committed the same address. That is the
    /// moment two real requests can reach at the same time, and the only one this suite cannot hit on
    /// purpose any other way.
    /// </summary>
    private sealed class CompetitorTakesTheAddressFirstInterceptor : DbCommandInterceptor
    {
        private readonly string _address;
        private readonly Func<Task> _takeTheAddress;
        private int _raced;

        public CompetitorTakesTheAddressFirstInterceptor(string address, Func<Task> takeTheAddress)
        {
            _address = address;
            _takeTheAddress = takeTheAddress;
        }

        public bool CompetitorCommitted { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (IsTheAddressUpdate(command) && Interlocked.CompareExchange(ref _raced, 1, 0) == 0)
            {
                await _takeTheAddress();
                CompetitorCommitted = true;
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private bool IsTheAddressUpdate(DbCommand command) =>
            IsUpdateOfUsers(command)
            && command.Parameters.Cast<DbParameter>()
                .Any(parameter => parameter.Value is string value
                                  && string.Equals(value, _address, StringComparison.Ordinal));
    }
}
