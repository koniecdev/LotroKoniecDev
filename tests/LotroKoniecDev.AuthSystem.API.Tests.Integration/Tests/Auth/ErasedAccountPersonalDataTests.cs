using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.IntegrationEvents;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// After the final erasure, the auth database holds nothing that names the person (ADR-0065, #1071):
/// not their addresses, not their username. The scan reads every text column of the schema, so a table
/// added later that keeps such data fails here instead of in a privacy complaint.
/// The relay runs in this suite against the spy publisher, but on its own clock, so a test that needs a
/// sent message marks it sent itself, the way the relay does.
/// </summary>
public sealed class ErasedAccountPersonalDataTests : EndpointsTestBase
{
    private const string TestPassword = "TestPass1!";
    private static readonly TimeSpan RelayReactionTimeout = TimeSpan.FromSeconds(15);

    private readonly SpyMessagePublisher _messagePublisherSpy;

    public ErasedAccountPersonalDataTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
        _messagePublisherSpy = appFactory.Services.GetRequiredService<SpyMessagePublisher>();
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _messagePublisherSpy.Reset();
    }

    public override Task DisposeAsync()
    {
        _messagePublisherSpy.Reset();
        return base.DisposeAsync();
    }

    [Fact]
    public async Task Finalizer_AfterAnEmailChange_ShouldLeaveNoTextColumnNamingThePerson()
    {
        // Arrange: the account moved from its first address to a second one, and both e-mail change
        // messages went out.
        (RegisterRequest registerRequest, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        string newEmail = $"nowy.{Guid.NewGuid():N}@shire.me";
        await EnqueueAsync(new EmailChangeRequested(identityId.Value, registerRequest.Email, newEmail));
        await EnqueueAsync(new EmailChangeCompleted(identityId.Value, registerRequest.Email, newEmail));
        await MarkEverySentAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));

        // Act
        int finalizedCount = await RunFinalizerAsync();

        // Assert
        finalizedCount.ShouldBe(1);
        (await FindTextColumnsHoldingAsync(registerRequest.Email)).ShouldBeEmpty();
        (await FindTextColumnsHoldingAsync(newEmail)).ShouldBeEmpty();
        (await FindTextColumnsHoldingAsync(registerRequest.Username)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Finalizer_ShouldWriteOneAccountErasedMessage_AndTheRelayShouldSendItToTheAccountEvents()
    {
        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));

        // Act
        await RunFinalizerAsync();
        SpyMessagePublisher.PublishedMessage? published = await WaitForPublishedAsync(nameof(AccountErased));

        // Assert: the erasure wrote it, and the reconciliation in the same run did not write a second one.
        OutboxMessage message = (await ReadMessagesAsync(nameof(AccountErased))).ShouldHaveSingleItem();
        JsonSerializer.Deserialize<AccountErased>(message.Payload).ShouldBe(new AccountErased(identityId.Value));
        published.ShouldNotBeNull();
        published.Exchange.ShouldBe("lotro.accounts");
        published.RoutingKey.ShouldBe("account.erased");
        published.MessageId.ShouldBe(message.Id);
    }

    [Fact]
    public async Task Finalizer_ForAnAccountThatIsNotDueYet_ShouldWriteNoAccountErasedMessage()
    {
        // Arrange: still inside its grace period.
        await RegisterAndScheduleDeletionAsync();

        // Act
        await RunFinalizerAsync();

        // Assert
        (await ReadMessagesAsync(nameof(AccountErased))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Finalizer_ShouldCutTheSentMessagesOfTheErasedAccountDownToItsId()
    {
        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await EnqueueAsync(new EmailChangeRequested(identityId.Value, registerRequest.Email, "nowy@shire.me"));
        await EnqueueAsync(new EmailChangeCompleted(identityId.Value, registerRequest.Email, "nowy@shire.me"));
        await MarkEverySentAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));

        // Act
        await RunFinalizerAsync();

        // Assert
        List<OutboxMessage> messages = await ReadMessagesAsync(nameof(EmailChangeRequested), nameof(EmailChangeCompleted));
        messages.Count.ShouldBe(2);
        messages.ShouldAllBe(message => message.ProcessedOn != null);
        foreach (OutboxMessage message in messages)
        {
            Dictionary<string, Guid>? payload = JsonSerializer.Deserialize<Dictionary<string, Guid>>(message.Payload);
            payload.ShouldBe(new Dictionary<string, Guid> { ["IdentityUserId"] = identityId.Value });
        }
    }

    [Fact]
    public async Task Finalizer_ShouldLeaveTheMessagesOfAnAccountThatStillExistsAsTheyWere()
    {
        // Arrange: another account changed its address, and one account is erased in the same run.
        (RegisterRequest liveRequest, IdentityId liveId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);
        await EnqueueAsync(new EmailChangeCompleted(liveId.Value, liveRequest.Email, "zywy@shire.me"));
        (_, IdentityId erasedId) = await RegisterAndScheduleDeletionAsync();
        await MarkEverySentAsync();
        await BackdateScheduleAsync(erasedId.Value, TimeSpan.FromDays(15));
        string before = (await ReadMessagesAsync(nameof(EmailChangeCompleted))).ShouldHaveSingleItem().Payload;

        // Act
        await RunFinalizerAsync();

        // Assert
        (await ReadMessagesAsync(nameof(EmailChangeCompleted))).ShouldHaveSingleItem().Payload.ShouldBe(before);
    }

    [Fact]
    public async Task Finalizer_ShouldLeaveAnUnsentMessageAlone_AndCutItOnceItIsSent()
    {
        // Arrange: the broker is down, so the relay has not sent the message when the account is
        // erased. A cut payload would reach the consumer as poison, so the run must wait for the relay.
        (RegisterRequest registerRequest, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await MarkEverySentAsync();
        _messagePublisherSpy.FailWith = new InvalidOperationException("broker down");
        await EnqueueAsync(new EmailChangeCompleted(identityId.Value, registerRequest.Email, "nowy@shire.me"));
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));

        // Act
        await RunFinalizerAsync();
        string unsentPayload = (await ReadMessagesAsync(nameof(EmailChangeCompleted))).ShouldHaveSingleItem().Payload;
        await MarkEverySentAsync();
        await RunFinalizerAsync();
        string sentPayload = (await ReadMessagesAsync(nameof(EmailChangeCompleted))).ShouldHaveSingleItem().Payload;

        // Assert
        unsentPayload.ShouldContain(registerRequest.Email);
        sentPayload.ShouldNotContain(registerRequest.Email);
        sentPayload.ShouldNotContain("nowy@shire.me");
    }

    [Fact]
    public async Task Finalizer_ShouldCutTheMessagesOfAnAccountErasedBeforeThisFix()
    {
        // Arrange: an account an earlier version erased, whose sent messages still hold both addresses,
        // and no account due in this run.
        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);
        await EnqueueAsync(new EmailChangeRequested(identityId.Value, registerRequest.Email, "stary@shire.me"));
        await MarkEverySentAsync();
        await AnonymizeByHandAsync(identityId.Value);

        // Act
        int finalizedCount = await RunFinalizerAsync();

        // Assert
        finalizedCount.ShouldBe(0);
        string payload = (await ReadMessagesAsync(nameof(EmailChangeRequested))).ShouldHaveSingleItem().Payload;
        payload.ShouldNotContain(registerRequest.Email);
        payload.ShouldNotContain("stary@shire.me");
    }

    [Fact]
    public async Task Reconcile_ForAnAccountErasedBeforeThisFix_ShouldTellTheTranslationSystemOnce()
    {
        // Arrange: an earlier version erased the account and never wrote an AccountErased.
        (_, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);
        await AnonymizeByHandAsync(identityId.Value);

        // Act
        Reconciliation first = await ReconcileAsync();
        Reconciliation second = await ReconcileAsync();

        // Assert
        first.ErasuresAnnounced.ShouldBe(1);
        second.ErasuresAnnounced.ShouldBe(0);
        OutboxMessage message = (await ReadMessagesAsync(nameof(AccountErased))).ShouldHaveSingleItem();
        JsonSerializer.Deserialize<AccountErased>(message.Payload).ShouldBe(new AccountErased(identityId.Value));
    }

    [Fact]
    public async Task Reconcile_ForAnAccountDeletedAtOnceBeforeTwoPhaseDeletion_ShouldCleanItAndTellTheTranslationSystem()
    {
        // Before #460 a deletion anonymized the account at once and wrote no deletion date.
        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);
        await EnqueueAsync(new EmailChangeCompleted(identityId.Value, registerRequest.Email, "nowy@shire.me"));
        await MarkEverySentAsync();
        await AnonymizeByHandAsync(identityId.Value, deletionScheduledAt: null);

        // Act
        Reconciliation reconciliation = await ReconcileAsync();

        // Assert
        reconciliation.ShouldBe(new Reconciliation(MessagesScrubbed: 1, ErasuresAnnounced: 1));
        (await ReadMessagesAsync(nameof(EmailChangeCompleted))).ShouldHaveSingleItem().Payload.ShouldNotContain(registerRequest.Email);
    }

    [Fact]
    public async Task Reconcile_ForALiveAccountOnTheAnonymizationDomain_ShouldTouchNothing()
    {
        // Arrange: the registration form accepts any address, so a live account can carry the marker
        // domain. It still has its password and no deletion date, so it is not erased.
        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);
        await EnqueueAsync(new EmailChangeCompleted(identityId.Value, registerRequest.Email, "nowy@shire.me"));
        await MarkEverySentAsync();
        await SetEmailAsync(
            identityId.Value,
            $"{AnonymizationConstants.EmailPrefix}{Guid.NewGuid():N}{AnonymizationConstants.EmailDomain}");

        // Act
        Reconciliation reconciliation = await ReconcileAsync();

        // Assert
        reconciliation.ShouldBe(new Reconciliation(MessagesScrubbed: 0, ErasuresAnnounced: 0));
        (await ReadMessagesAsync(nameof(EmailChangeCompleted))).ShouldHaveSingleItem().Payload.ShouldContain("nowy@shire.me");
        (await ReadMessagesAsync(nameof(AccountErased))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Reconcile_WithARowThatIsNotJson_ShouldStillCutTheOthers()
    {
        // Arrange: one row the scrub cannot read must not stop it for every account.
        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);
        await EnqueueAsync(new EmailChangeRequested(identityId.Value, registerRequest.Email, "nowy@shire.me"));
        await AddRawMessageAsync(nameof(EmailChangeRequested), "this is not json");
        await AddRawMessageAsync(nameof(EmailChangeRequested), "\"a json string\"");
        await MarkEverySentAsync();
        await AnonymizeByHandAsync(identityId.Value);

        // Act
        Reconciliation reconciliation = await ReconcileAsync();

        // Assert
        reconciliation.MessagesScrubbed.ShouldBe(1);
        List<string> payloads = (await ReadMessagesAsync(nameof(EmailChangeRequested)))
            .Select(message => message.Payload)
            .ToList();
        payloads.ShouldContain("this is not json");
        payloads.ShouldContain("\"a json string\"");
        payloads.ShouldNotContain(payload => payload.Contains(registerRequest.Email));
    }

    [Fact]
    public async Task Reconcile_RunTwice_ShouldChangeNothingTheSecondTime()
    {
        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);
        await EnqueueAsync(new EmailChangeCompleted(identityId.Value, registerRequest.Email, "nowy@shire.me"));
        await MarkEverySentAsync();
        await AnonymizeByHandAsync(identityId.Value);
        Reconciliation first = await ReconcileAsync();
        string afterFirst = (await ReadMessagesAsync(nameof(EmailChangeCompleted))).ShouldHaveSingleItem().Payload;

        // Act
        Reconciliation second = await ReconcileAsync();

        // Assert
        first.MessagesScrubbed.ShouldBe(1);
        second.MessagesScrubbed.ShouldBe(0);
        (await ReadMessagesAsync(nameof(EmailChangeCompleted))).ShouldHaveSingleItem().Payload.ShouldBe(afterFirst);
    }

    /// <summary>
    /// Every text column of the auth schema whose value holds <paramref name="value"/>, compared without
    /// case, because Identity keeps an upper-case copy of the address and the username.
    /// </summary>
    private async Task<List<string>> FindTextColumnsHoldingAsync(string value)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        NpgsqlConnection connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();

        List<(string Table, string Column)> columns = [];
        await using (NpgsqlCommand listColumns = new(
            """
            SELECT table_name, column_name
            FROM information_schema.columns
            WHERE table_schema = @schema
              AND data_type IN ('text', 'character varying', 'character', 'json', 'jsonb')
            """,
            connection))
        {
            listColumns.Parameters.AddWithValue("schema", DatabaseSchemas.Auth);
            await using NpgsqlDataReader reader = await listColumns.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        columns.Count.ShouldBeGreaterThan(10);

        List<string> holding = [];
        foreach ((string table, string column) in columns)
        {
            await using NpgsqlCommand count = new(
                $"""SELECT count(*) FROM "{DatabaseSchemas.Auth}"."{table}" WHERE "{column}"::text ILIKE @pattern""",
                connection);
            count.Parameters.AddWithValue("pattern", "%" + value + "%");
            if ((long)(await count.ExecuteScalarAsync() ?? 0L) > 0)
            {
                holding.Add($"{table}.{column}");
            }
        }

        return holding;
    }

    private async Task EnqueueAsync<TMessage>(TMessage message) where TMessage : class
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        OutboxWriter writer = scope.ServiceProvider.GetRequiredService<OutboxWriter>();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        writer.Enqueue(message);
        await db.SaveChangesAsync();
    }

    private async Task AddRawMessageAsync(string type, string payload)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        db.OutboxMessages.Add(OutboxMessage.Create(type, payload, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// What the relay does after a publish the broker confirmed.
    /// </summary>
    private async Task MarkEverySentAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        List<OutboxMessage> unsent = await db.OutboxMessages.Where(message => message.ProcessedOn == null).ToListAsync();
        foreach (OutboxMessage message in unsent)
        {
            message.MarkAsProcessed(DateTimeOffset.UtcNow);
        }

        await db.SaveChangesAsync();
    }

    private async Task<List<OutboxMessage>> ReadMessagesAsync(params string[] types)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.OutboxMessages.AsNoTracking()
            .Where(message => types.Contains(message.Type))
            .ToListAsync();
    }

    /// <summary>
    /// The shape an account had after an erasure by an earlier version: the marker address and no
    /// password, written straight to the row and not through the erasure. A two-phase deletion also
    /// left a deletion date, and the immediate deletion before #460 left none.
    /// </summary>
    private Task AnonymizeByHandAsync(Guid userId) =>
        AnonymizeByHandAsync(userId, DateTimeOffset.UtcNow.AddDays(-20));

    private async Task AnonymizeByHandAsync(Guid userId, DateTimeOffset? deletionScheduledAt)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        ApplicationUser user = await db.Users.SingleAsync(row => row.Id == userId);
        string anonymizedEmail = $"{AnonymizationConstants.EmailPrefix}{Guid.NewGuid():N}{AnonymizationConstants.EmailDomain}";
        user.Email = anonymizedEmail;
        user.NormalizedEmail = anonymizedEmail.ToUpperInvariant();
        user.PasswordHash = null;
        user.DeletionScheduledAt = deletionScheduledAt;
        await db.SaveChangesAsync();
    }

    private async Task SetEmailAsync(Guid userId, string email)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        ApplicationUser user = await db.Users.SingleAsync(row => row.Id == userId);
        user.Email = email;
        user.NormalizedEmail = email.ToUpperInvariant();
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Both steps, in the order the finalizer runs them.
    /// </summary>
    private async Task<Reconciliation> ReconcileAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        IErasedAccountReconciler reconciler = scope.ServiceProvider.GetRequiredService<IErasedAccountReconciler>();
        int scrubbed = await reconciler.ScrubSentMessagesAsync(CancellationToken.None);
        int announced = await reconciler.AnnounceUnannouncedErasuresAsync(CancellationToken.None);
        return new Reconciliation(scrubbed, announced);
    }

    private sealed record Reconciliation(int MessagesScrubbed, int ErasuresAnnounced);

    private async Task<SpyMessagePublisher.PublishedMessage?> WaitForPublishedAsync(string type)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + RelayReactionTimeout;

        while (true)
        {
            SpyMessagePublisher.PublishedMessage? match = _messagePublisherSpy.Published
                .FirstOrDefault(message => message.Type == type);

            if (match is not null || DateTimeOffset.UtcNow > deadline)
            {
                return match;
            }

            await Task.Delay(100);
        }
    }

    private async Task<int> RunFinalizerAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        IAccountDeletionFinalizer finalizer = scope.ServiceProvider.GetRequiredService<IAccountDeletionFinalizer>();
        return await finalizer.FinalizeDueAccountsAsync(CancellationToken.None);
    }

    private async Task BackdateScheduleAsync(Guid userId, TimeSpan age)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        ApplicationUser user = await db.Users.SingleAsync(row => row.Id == userId);
        user.DeletionScheduledAt = DateTimeOffset.UtcNow - age;
        await db.SaveChangesAsync();
    }

    private async Task<(RegisterRequest Request, IdentityId IdentityId)> RegisterAndScheduleDeletionAsync()
    {
        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        using HttpRequestMessage request = new(HttpMethod.Post, "auth/account/delete");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new DeleteAccountRequest(TestPassword));

        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        return (registerRequest, identityId);
    }
}
