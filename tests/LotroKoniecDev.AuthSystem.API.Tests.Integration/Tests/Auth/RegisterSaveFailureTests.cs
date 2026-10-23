using System.Data.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// A registration whose save fails. Only a real duplicate may come back as a taken e-mail or username.
/// A passing database error has to reach the execution strategy, which replays the registration, and any
/// other error must not be dressed up as a taken address (#845).
/// </summary>
public sealed class RegisterSaveFailureTests : EndpointsTestBase
{
    private static readonly Uri RegisterEndpoint = new("auth/register", UriKind.Relative);

    public RegisterSaveFailureTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    /// <summary>
    /// One write for each save in the registration: the account, its role, and the confirmation mail.
    /// </summary>
    [Theory]
    [InlineData("Users")]
    [InlineData("UserRoles")]
    [InlineData("OutboxMessages")]
    public async Task Register_ShouldReturnAccepted_WhenATransientFailureHitsAWrite(string table)
    {
        // Arrange
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);
        Factory.DbCommandFailures.FailNext(command => IsInsertInto(command, table), CreateTransientFailure);

        // Act
        using HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(RegisterEndpoint, request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser? user = await userManager.FindByEmailAsync(request.Email);
        user.ShouldNotBeNull();
        (await userManager.IsInRoleAsync(user, AuthConstants.Roles.Translator)).ShouldBeTrue();
    }

    /// <summary>
    /// A taken address writes only the notice to its owner (#860), and a passing failure on that write is
    /// replayed like the others: the caller gets the same 202, and the owner gets one notice, not two.
    /// </summary>
    [Fact]
    public async Task Register_ShouldQueueOneNotice_WhenATransientFailureHitsTheNoticeWrite()
    {
        // Arrange
        (RegisterRequest existing, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker) with { Email = existing.Email };
        Factory.DbCommandFailures.FailNext(command => IsInsertInto(command, "OutboxMessages"), CreateTransientFailure);

        // Act
        using HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(RegisterEndpoint, request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        (await db.OutboxMessages.AsNoTracking()
            .CountAsync(row => row.Type == nameof(RegistrationAttemptedOnExistingAccount))).ShouldBe(1);
    }

    /// <summary>
    /// A permanent error is an outage from the caller's side, so it is a 500. A unique violation on an
    /// index the handler cannot place is not proof that the address is taken either.
    /// </summary>
    [Theory]
    [InlineData(PostgresErrorCodes.NotNullViolation, null)]
    [InlineData(PostgresErrorCodes.UniqueViolation, "IX_NotAUsersIndex")]
    public async Task Register_ShouldReturnInternalServerErrorAndCreateNoAccount_WhenAWriteFailsForAnotherReason(
        string sqlState,
        string? constraintName)
    {
        // Arrange
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);
        Factory.DbCommandFailures.FailNext(
            command => IsInsertInto(command, "Users"),
            () => new PostgresException(
                "simulated permanent failure", "ERROR", "ERROR", sqlState, constraintName: constraintName));

        // Act
        using HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(RegisterEndpoint, request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        (await userManager.FindByEmailAsync(request.Email)).ShouldBeNull();
    }

    /// <summary>
    /// The Polish error page is for a browser only (#867). The frontend's back-channel sends the first
    /// value, and curl or fetch send <c>*/*</c>. Each of them keeps the problem details it can parse.
    /// </summary>
    [Theory]
    [InlineData("application/vnd.dev-lotrokoniecdev.hateoas.json")]
    [InlineData("application/json")]
    [InlineData("*/*")]
    [InlineData(null)]
    public async Task Register_ShouldAnswerAnApiClientWithProblemDetails_WhenAWriteFailsForAnotherReason(string? accept)
    {
        // Arrange
        RegisterRequest registerRequest = UserFactory.GenerateRandomRegisterRequest(Faker);
        Factory.DbCommandFailures.FailNext(
            command => IsInsertInto(command, "Users"),
            () => new PostgresException(
                "simulated permanent failure", "ERROR", "ERROR", PostgresErrorCodes.NotNullViolation));

        using HttpRequestMessage request = new(HttpMethod.Post, RegisterEndpoint);
        request.Content = JsonContent.Create(registerRequest);
        if (accept is not null)
        {
            request.Headers.Accept.ParseAdd(accept);
        }

        // Act
        using HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/problem+json");
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("errorCode").GetString().ShouldBe("Internal.UnhandledException");
    }

    /// <summary>
    /// Both requests pass every check before either writes, so the unique index decides, and both letter
    /// cases are raced. A lost username says so, because it is a public handle. A lost address answers as a
    /// taken one: the same 202, and a permit spent (#860).
    /// </summary>
    [Theory]
    [InlineData(Clash.Email, false, HttpStatusCode.Accepted, null)]
    [InlineData(Clash.EmailInOtherCase, false, HttpStatusCode.Accepted, null)]
    [InlineData(Clash.Email, true, HttpStatusCode.TooManyRequests, "Auth.RegistrationMailboxThrottled")]
    [InlineData(Clash.Username, false, HttpStatusCode.UnprocessableEntity, "Auth.UserAlreadyExistsByUsername")]
    [InlineData(Clash.UsernameInOtherCase, false, HttpStatusCode.UnprocessableEntity, "Auth.UserAlreadyExistsByUsername")]
    public async Task Register_ShouldAnswerAsTheCheckWould_WhenARegistrationCommitsTheSameValueJustBeforeTheInsert(
        Clash clash,
        bool budgetSpent,
        HttpStatusCode expectedStatus,
        string? expectedErrorCode)
    {
        // Arrange: the competitor goes through the suite's main host, whose contexts do not carry this
        // interceptor, so it registers normally while our request waits in front of its insert
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);
        RegisterRequest competitor = clash switch
        {
            Clash.Email => UserFactory.GenerateRandomRegisterRequest(Faker) with { Email = request.Email },
            Clash.EmailInOtherCase => UserFactory.GenerateRandomRegisterRequest(Faker) with
            {
                Email = request.Email.ToUpperInvariant()
            },
            Clash.Username => UserFactory.GenerateRandomRegisterRequest(Faker) with { Username = request.Username },
            Clash.UsernameInOtherCase => UserFactory.GenerateRandomRegisterRequest(Faker) with
            {
                Username = request.Username.ToUpperInvariant()
            },
            _ => throw new ArgumentOutOfRangeException(nameof(clash), clash, null)
        };

        CompetitorCommitsFirstInterceptor interceptor = new(
            command => IsTheAccountInsert(command, request.Email),
            async () =>
            {
                using HttpResponseMessage response =
                    await ApiClient.Http.PostAsJsonAsync(RegisterEndpoint, competitor);
                return response.StatusCode;
            });

        await using WebApplicationFactory<Program> host = Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // A second relay on this database could take a row another test waits for.
                AuthSystemApiFactory.RemoveHostedService<OutboxRelay>(services);
                services.ConfigureDbContext<AuthDbContext>(options => options.AddInterceptors(interceptor));
            }));
        using HttpClient client = host.CreateClient();
        if (budgetSpent)
        {
            AccountStateFactory.SpendRegistrationBudget(host.Services, request.Email);
        }

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(RegisterEndpoint, request);

        // Assert
        interceptor.CompetitorStatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.StatusCode.ShouldBe(expectedStatus);
        string content = await response.Content.ReadAsStringAsync();
        if (expectedErrorCode is null)
        {
            content.ShouldBeEmpty();
        }
        else
        {
            content.ShouldContain(expectedErrorCode);
        }
    }

    /// <summary>
    /// Identity looks the address up again inside <c>CreateAsync</c>. A registration that commits the address
    /// in between makes it refuse with <c>DuplicateEmail</c>, which must answer as a taken address, never
    /// with the general message that names it (#860).
    /// </summary>
    [Theory]
    [InlineData(false, HttpStatusCode.Accepted)]
    [InlineData(true, HttpStatusCode.TooManyRequests)]
    public async Task Register_ShouldAnswerAsATakenAddress_WhenIdentitysOwnCheckFindsTheAddressJustTaken(
        bool budgetSpent,
        HttpStatusCode expectedStatus)
    {
        // Arrange: the competitor commits while our request checks the reservation, which sits between
        // our address lookup and Identity's
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);
        RegisterRequest competitor = UserFactory.GenerateRandomRegisterRequest(Faker) with { Email = request.Email };

        CompetitorCommitsFirstInterceptor interceptor = new(
            command => IsTheReservationCheck(command, request.Email),
            async () =>
            {
                using HttpResponseMessage response =
                    await ApiClient.Http.PostAsJsonAsync(RegisterEndpoint, competitor);
                return response.StatusCode;
            });

        await using WebApplicationFactory<Program> host = Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // A second relay on this database could take a row another test waits for.
                AuthSystemApiFactory.RemoveHostedService<OutboxRelay>(services);
                services.ConfigureDbContext<AuthDbContext>(options => options.AddInterceptors(interceptor));
            }));
        using HttpClient client = host.CreateClient();
        if (budgetSpent)
        {
            AccountStateFactory.SpendRegistrationBudget(host.Services, request.Email);
        }

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(RegisterEndpoint, request);

        // Assert
        interceptor.CompetitorStatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.StatusCode.ShouldBe(expectedStatus);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(request.Email, Case.Insensitive);

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        (await db.Users.AsNoTracking().AnyAsync(user => user.UserName == request.Username)).ShouldBeFalse();
        (await db.Users.AsNoTracking().AnyAsync(user => user.UserName == competitor.Username)).ShouldBeTrue();
    }

    public enum Clash
    {
        Email,
        EmailInOtherCase,
        Username,
        UsernameInOtherCase
    }

    /// <summary>
    /// Matches the insert itself. A batch that only mentions the table, such as the role save that also
    /// updates the account row, does not count.
    /// </summary>
    private static bool IsInsertInto(DbCommand command, string table) =>
        command.CommandText.Contains($"INSERT INTO {DatabaseSchemas.Auth}.\"{table}\"", StringComparison.Ordinal);

    private static bool IsTheAccountInsert(DbCommand command, string email) =>
        IsInsertInto(command, "Users")
        && command.Parameters.Cast<DbParameter>()
            .Any(parameter => parameter.Value is string value
                              && string.Equals(value, email, StringComparison.Ordinal));

    /// <summary>
    /// The reservation check filters on the undo target; the account lookups only list that column. The
    /// address has to match too, because the admin seed runs the same check when the host starts.
    /// </summary>
    private static bool IsTheReservationCheck(DbCommand command, string email) =>
        command.CommandText.Contains("\"NormalizedEmailChangeRevertTo\" = ", StringComparison.Ordinal)
        && command.Parameters.Cast<DbParameter>()
            .Any(parameter => parameter.Value is string value
                              && string.Equals(value, email, StringComparison.OrdinalIgnoreCase));

    private static NpgsqlException CreateTransientFailure() =>
        new("The operation has timed out", new TimeoutException());

    /// <summary>
    /// Holds one chosen command until a competing registration has committed, the only race this suite
    /// cannot reach otherwise.
    /// </summary>
    private sealed class CompetitorCommitsFirstInterceptor : DbCommandInterceptor
    {
        private readonly Func<DbCommand, bool> _holdsBefore;
        private readonly Func<Task<HttpStatusCode>> _registerCompetitor;
        private int _raced;

        public CompetitorCommitsFirstInterceptor(
            Func<DbCommand, bool> holdsBefore,
            Func<Task<HttpStatusCode>> registerCompetitor)
        {
            _holdsBefore = holdsBefore;
            _registerCompetitor = registerCompetitor;
        }

        public HttpStatusCode? CompetitorStatusCode { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (_holdsBefore(command) && Interlocked.CompareExchange(ref _raced, 1, 0) == 0)
            {
                CompetitorStatusCode = await _registerCompetitor();
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
