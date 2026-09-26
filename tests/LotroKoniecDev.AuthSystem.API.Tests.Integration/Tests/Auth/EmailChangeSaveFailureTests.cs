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
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// An e-mail change whose save fails while it is confirmed or undone. Only a taken address may come back
/// as a taken address: a duplicate on an e-mail index (#864), or Identity's own check inside the save
/// finding the address taken (#866). Any other error is an outage: a 500, and nothing changes.
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
        using HttpResponseMessage response = await PostToPageAsync(
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
        using HttpResponseMessage response = await PostToPageAsync(
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
        using HttpResponseMessage response = await PostToPageAsync(
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
        using HttpResponseMessage response = await PostToPageAsync(
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
    /// answer, where an outage is a 500. A competitor who writes the address in other letter case clashes
    /// only on the case-blind index of ADR-0022, so both spellings are raced.
    /// </summary>
    [Theory]
    [InlineData(Spelling.Exact)]
    [InlineData(Spelling.OtherCase)]
    public async Task ConfirmPage_Post_ShouldRefuseAndKeepTheOldAddress_WhenAnotherAccountTakesTheAddressJustBeforeTheSave(
        Spelling spelling)
    {
        // Arrange: the competitor is created through the suite's main host, whose contexts do not carry
        // this interceptor, so it commits while our update waits
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        Guid userId = await UserIdOfAsync(user.Email);
        string newEmail = Faker.Internet.Email(uniqueSuffix: Guid.CreateVersion7().ToString("N"));

        CompetitorTakesTheAddressFirstInterceptor interceptor = new(
            newEmail, RaceMoment.BeforeTheUpdate, () => SeedUserOnAsync(Spell(newEmail, spelling)));
        await using WebApplicationFactory<Program> host = CreateHostWith(interceptor);
        using HttpClient client = host.CreateClient();

        string token = await CreateTokenAsync(
            host.Services,
            userId,
            EmailChangeTokenProvider.ProviderName,
            EmailChangeTokenProvider.PurposeFor(newEmail));

        // Act
        using HttpResponseMessage response = await PostToPageAsync(
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

    [Theory]
    [InlineData(Spelling.Exact)]
    [InlineData(Spelling.OtherCase)]
    public async Task RevertPage_Post_ShouldRefuseAndKeepThePassword_WhenAnotherAccountTakesThePreviousAddressJustBeforeTheSave(
        Spelling spelling)
    {
        // Arrange
        (RegisterRequest user, string newEmail, Guid userId) = await CompleteChangeAsync();

        CompetitorTakesTheAddressFirstInterceptor interceptor = new(
            user.Email, RaceMoment.BeforeTheUpdate, () => SeedUserOnAsync(Spell(user.Email, spelling)));
        await using WebApplicationFactory<Program> host = CreateHostWith(interceptor);
        using HttpClient client = host.CreateClient();

        string revertToken = await CreateTokenAsync(
            host.Services,
            userId,
            EmailChangeRevertTokenProvider.ProviderName,
            EmailChangeRevertTokenProvider.PurposeFor(user.Email, newEmail));

        // Act
        using HttpResponseMessage response = await PostToPageAsync(
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

    /// <summary>
    /// <c>UpdateAsync</c> looks the address up once more before it writes. A competitor who commits
    /// between the handler's lookup and that one is refused by Identity, not by the index, and the page
    /// must still say the address is taken rather than call a good link dead (#866).
    /// </summary>
    [Theory]
    [InlineData(Spelling.Exact)]
    [InlineData(Spelling.OtherCase)]
    public async Task RevertPage_Post_ShouldRefuseAndKeepThePassword_WhenAnotherAccountTakesThePreviousAddressDuringIdentitysOwnCheck(
        Spelling spelling)
    {
        // Arrange
        (RegisterRequest user, string newEmail, Guid userId) = await CompleteChangeAsync();

        CompetitorTakesTheAddressFirstInterceptor interceptor = new(
            user.Email, RaceMoment.BeforeIdentitysOwnCheck, () => SeedUserOnAsync(Spell(user.Email, spelling)));
        await using WebApplicationFactory<Program> host = CreateHostWith(interceptor);
        using HttpClient client = host.CreateClient();

        string revertToken = await CreateTokenAsync(
            host.Services,
            userId,
            EmailChangeRevertTokenProvider.ProviderName,
            EmailChangeRevertTokenProvider.PurposeFor(user.Email, newEmail));

        // Act
        using HttpResponseMessage response = await PostToPageAsync(
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

    /// <summary>
    /// The confirm page shows one message for every refusal, so the handler's answer is checked
    /// directly. It must be the same taken address the index race gets, not a general failure.
    /// </summary>
    [Theory]
    [InlineData(Spelling.Exact)]
    [InlineData(Spelling.OtherCase)]
    public async Task ConfirmHandler_Handle_ShouldReturnAddressTakenAndKeepTheOldAddress_WhenAnotherAccountTakesTheAddressDuringIdentitysOwnCheck(
        Spelling spelling)
    {
        // Arrange
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        Guid userId = await UserIdOfAsync(user.Email);
        string newEmail = Faker.Internet.Email(uniqueSuffix: Guid.CreateVersion7().ToString("N"));

        CompetitorTakesTheAddressFirstInterceptor interceptor = new(
            newEmail, RaceMoment.BeforeIdentitysOwnCheck, () => SeedUserOnAsync(Spell(newEmail, spelling)));
        await using WebApplicationFactory<Program> host = CreateHostWith(interceptor);

        string token = await CreateTokenAsync(
            host.Services,
            userId,
            EmailChangeTokenProvider.ProviderName,
            EmailChangeTokenProvider.PurposeFor(newEmail));

        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        ICommandHandler<ConfirmEmailChange.Command, Result> handler =
            scope.ServiceProvider.GetRequiredService<ICommandHandler<ConfirmEmailChange.Command, Result>>();

        // Act
        Result result = await handler.Handle(
            new ConfirmEmailChange.Command(userId.ToString(), newEmail, token, null, null),
            CancellationToken.None);

        // Assert
        interceptor.CompetitorCommitted.ShouldBeTrue();
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(AuthErrors.UserAlreadyExistsByEmail);
        (await LoadUserByIdAsync(userId)).Email.ShouldBe(user.Email);
    }

    public enum Spelling
    {
        Exact,
        OtherCase
    }

    private enum RaceMoment
    {
        BeforeTheUpdate,
        BeforeIdentitysOwnCheck
    }

    private static string Spell(string address, Spelling spelling) =>
        spelling switch
        {
            Spelling.Exact => address,
            Spelling.OtherCase => address.ToUpperInvariant(),
            _ => throw new ArgumentOutOfRangeException(nameof(spelling), spelling, null)
        };

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
        string newEmail = Faker.Internet.Email(uniqueSuffix: Guid.CreateVersion7().ToString("N"));

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

        using HttpResponseMessage response = await PostToPageAsync(
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
        using HttpResponseMessage pageResponse = await client.GetAsync(new Uri(getUrl, UriKind.Relative));
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
    /// Holds one step of the account's save until another account has committed the same address. That
    /// is the moment two real requests can reach at the same time, and the only one this suite cannot hit
    /// on purpose any other way.
    /// </summary>
    private sealed class CompetitorTakesTheAddressFirstInterceptor : DbCommandInterceptor
    {
        private readonly string _address;
        private readonly RaceMoment _moment;
        private readonly Func<Task> _takeTheAddress;
        private int _raced;

        public CompetitorTakesTheAddressFirstInterceptor(string address, RaceMoment moment, Func<Task> takeTheAddress)
        {
            _address = address;
            _moment = moment;
            _takeTheAddress = takeTheAddress;
        }

        public bool CompetitorCommitted { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (IsTheMoment(command, eventData) && Interlocked.CompareExchange(ref _raced, 1, 0) == 0)
            {
                await _takeTheAddress();
                CompetitorCommitted = true;
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private bool IsTheMoment(DbCommand command, CommandEventData eventData) =>
            _moment switch
            {
                RaceMoment.BeforeTheUpdate => IsTheAddressUpdate(command),
                RaceMoment.BeforeIdentitysOwnCheck => IsIdentitysOwnCheck(command, eventData),
                _ => throw new ArgumentOutOfRangeException(nameof(_moment), _moment, null)
            };

        private bool IsTheAddressUpdate(DbCommand command) =>
            IsUpdateOfUsers(command)
            && command.Parameters.Cast<DbParameter>()
                .Any(parameter => parameter.Value is string value
                                  && string.Equals(value, _address, StringComparison.Ordinal));

        /// <summary>
        /// The handler looks the address up too, with the same query. Identity's lookup is the one that
        /// runs once the handler has already put the address on the account.
        /// </summary>
        private bool IsIdentitysOwnCheck(DbCommand command, CommandEventData eventData) =>
            command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.Ordinal)
            && command.CommandText.Contains("\"NormalizedEmail\" = ", StringComparison.Ordinal)
            && command.Parameters.Cast<DbParameter>()
                .Any(parameter => parameter.Value is string value
                                  && string.Equals(value, _address, StringComparison.OrdinalIgnoreCase))
            && eventData.Context is not null
            && eventData.Context.ChangeTracker.Entries<ApplicationUser>()
                .Any(entry => string.Equals(entry.Entity.Email, _address, StringComparison.OrdinalIgnoreCase));
    }
}
