using System.Data.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
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
    public async Task Register_ShouldReturnCreated_WhenATransientFailureHitsAWrite(string table)
    {
        // Arrange
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);
        Factory.DbCommandFailures.FailNext(command => IsInsertInto(command, table), CreateTransientFailure);

        // Act
        using HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(RegisterEndpoint, request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser? user = await userManager.FindByEmailAsync(request.Email);
        user.ShouldNotBeNull();
        (await userManager.IsInRoleAsync(user, AuthConstants.Roles.Translator)).ShouldBeTrue();
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
    /// Both requests pass every check before either one writes, so the unique index decides. The losing
    /// request must name the field it really lost on.
    /// </summary>
    [Theory]
    [InlineData(Clash.Email, "Auth.UserAlreadyExistsByEmail")]
    [InlineData(Clash.Username, "Auth.UserAlreadyExistsByUsername")]
    public async Task Register_ShouldNameTheTakenField_WhenARegistrationCommitsTheSameValueJustBeforeTheInsert(
        Clash clash,
        string expectedErrorCode)
    {
        // Arrange: the competitor goes through the suite's main host, whose contexts do not carry this
        // interceptor, so it registers normally while our request waits in front of its insert
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);
        RegisterRequest competitor = clash switch
        {
            Clash.Email => UserFactory.GenerateRandomRegisterRequest(Faker) with { Email = request.Email },
            Clash.Username => UserFactory.GenerateRandomRegisterRequest(Faker) with { Username = request.Username },
            _ => throw new ArgumentOutOfRangeException(nameof(clash), clash, null)
        };

        CompetitorCommitsFirstInterceptor interceptor = new(
            request.Email,
            async () =>
            {
                using HttpResponseMessage response =
                    await ApiClient.Http.PostAsJsonAsync(RegisterEndpoint, competitor);
                return response.StatusCode;
            });

        await using WebApplicationFactory<Program> host = Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.ConfigureDbContext<AuthDbContext>(options => options.AddInterceptors(interceptor))));
        using HttpClient client = host.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(RegisterEndpoint, request);

        // Assert
        interceptor.CompetitorStatusCode.ShouldBe(HttpStatusCode.Created);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        string content = await response.Content.ReadAsStringAsync();
        content.ShouldContain(expectedErrorCode);
    }

    public enum Clash
    {
        Email,
        Username
    }

    private static bool IsInsertInto(DbCommand command, string table) =>
        command.CommandText.Contains("INSERT INTO", StringComparison.Ordinal)
        && command.CommandText.Contains($"\"{table}\"", StringComparison.Ordinal);

    private static NpgsqlException CreateTransientFailure() =>
        new("The operation has timed out", new TimeoutException());

    /// <summary>
    /// Holds the insert of one registration's account until a competing registration has committed.
    /// That is the moment two real requests can reach at the same time, and the only one this suite
    /// cannot hit on purpose any other way.
    /// </summary>
    private sealed class CompetitorCommitsFirstInterceptor : DbCommandInterceptor
    {
        private readonly string _email;
        private readonly Func<Task<HttpStatusCode>> _registerCompetitor;
        private int _raced;

        public CompetitorCommitsFirstInterceptor(string email, Func<Task<HttpStatusCode>> registerCompetitor)
        {
            _email = email;
            _registerCompetitor = registerCompetitor;
        }

        public HttpStatusCode? CompetitorStatusCode { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (IsTheAccountInsert(command) && Interlocked.CompareExchange(ref _raced, 1, 0) == 0)
            {
                CompetitorStatusCode = await _registerCompetitor();
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private bool IsTheAccountInsert(DbCommand command) =>
            IsInsertInto(command, "Users")
            && command.Parameters.Cast<DbParameter>()
                .Any(parameter => parameter.Value is string value
                                  && string.Equals(value, _email, StringComparison.Ordinal));
    }
}
