using System.Collections;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using RabbitMQ.Client;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Messaging;

/// <summary>
/// The account-deletion parts of the pipeline with a real broker (ADR-0038). The tests follow
/// <see cref="PasswordResetPipelineTests"/> where the machinery is the same, and add: the cancel token made at
/// delivery must cancel the deletion, the token in the cancel response must finish the recovery, and a
/// message whose reason disappeared before delivery is acknowledged and not sent.
/// </summary>
public sealed class AccountDeletionPipelineTests : IClassFixture<BrokeredAuthSystemApiFactory>, IAsyncLifetime
{
    private const string TestPassword = "TestPass1!";

    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan EmptyQueueGrace = TimeSpan.FromSeconds(2);

    private readonly BrokeredAuthSystemApiFactory _factory;
    private readonly TestApiClient _apiClient;
    private readonly SpyAccountConfirmationEmailSender _confirmationEmailSpy;
    private readonly SpyAccountDeletionEmailSender _deletionEmailSpy;
    private readonly Bogus.Faker _faker = new();

    public AccountDeletionPipelineTests(BrokeredAuthSystemApiFactory factory)
    {
        _factory = factory;
        _confirmationEmailSpy = factory.Services.GetRequiredService<SpyAccountConfirmationEmailSender>();
        _deletionEmailSpy = factory.Services.GetRequiredService<SpyAccountDeletionEmailSender>();

        JsonSerializerOptions jsonSerializerOptions =
            factory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        _apiClient = new TestApiClient(factory.CreateClient(), jsonSerializerOptions);
    }

    public async Task InitializeAsync()
    {
        _confirmationEmailSpy.Reset();
        _deletionEmailSpy.Reset();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        CleanerService cleaner = scope.ServiceProvider.GetRequiredService<CleanerService>();
        await cleaner.CleanAsync();

        // The consumer declares the topology on startup, but the first test may get here first. Declaring
        // it again is safe (DeadLetterTopologyTests).
        await using IConnection connection = await _factory.Broker.ConnectAsync(CancellationToken.None);
        await using IChannel channel = await connection.CreateChannelAsync();
        await RabbitMqTopologyDeclaration.DeclareAsync(channel, CancellationToken.None);
        await channel.QueuePurgeAsync(RabbitMqTopology.EmailQueue);
        await channel.QueuePurgeAsync(RabbitMqTopology.EmailDeadLetterQueue);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Pipeline_ShouldDeliverAScheduledEmailWhoseCancelLinkWorks_WhenDeleteAccountCommits()
    {
        // Arrange: a confirmed account, so the only pipeline traffic left is the deletion e-mail
        (RegisterRequest request, IdentityId identityId) = await UserFactory.RegisterRandomUserWithRequestAsync(
            _apiClient, _faker, _confirmationEmailSpy, TestPassword);
        string accessToken = await GetAccessTokenAsync(request.Email);

        // Act: deleting the account is the user-facing trigger of the whole pipeline
        HttpResponseMessage deleteResponse = await SendDeleteRequestAsync(accessToken);
        await _deletionEmailSpy.WaitForScheduledCaptureAsync(request.Email, DeliveryTimeout);

        // Assert: the e-mail went out once, to the registered address, with the recomputed date
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        SpyAccountDeletionEmailSender.ScheduledEmail scheduledEmail =
            _deletionEmailSpy.ScheduledEmailsTo(request.Email).ShouldHaveSingleItem();
        scheduledEmail.CancelToken.ShouldNotBeNullOrWhiteSpace();
        scheduledEmail.FinalizesAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // The delivered token really cancels the deletion, so a token made at delivery works.
        HttpResponseMessage cancelResponse = await _apiClient.Http.PostAsJsonAsync(
            new Uri("auth/account/cancel-deletion", UriKind.Relative),
            new CancelAccountDeletionRequest(request.Email, scheduledEmail.CancelToken));
        cancelResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Waiting for the cancel's own mail keeps it out of the poison theory's count of mails.
        await _deletionEmailSpy.WaitForCancelledCaptureAsync(request.Email, DeliveryTimeout);
        _deletionEmailSpy.CancelledEmailCountTo(request.Email).ShouldBe(1);

        // The outbox row was published and marked on the first attempt, and it carries the user id and
        // nothing else: a cancel token must never sit in an outbox row (ADR-0038 decision 2).
        OutboxMessage? outboxRow = await OutboxAssertions.WaitForOutboxRowAsync(
            _factory,
            row => row.Type == nameof(AccountDeletionScheduled) && row.ProcessedOn != null,
            DeliveryTimeout);
        outboxRow.ShouldNotBeNull();
        outboxRow.Attempts.ShouldBe(0);
        outboxRow.LastError.ShouldBeNull();
        AccountDeletionScheduled payload = JsonSerializer.Deserialize<AccountDeletionScheduled>(outboxRow.Payload)
            .ShouldNotBeNull();
        payload.IdentityUserId.ShouldBe(identityId.Value);
        outboxRow.Payload.ShouldNotContain(scheduledEmail.CancelToken);

        // The delivery was recorded for deduplication and nothing was left on the broker
        (await OutboxAssertions.CountInboxRowsAsync(_factory, outboxRow.Id)).ShouldBe(1);
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailDeadLetterQueue, EmptyQueueGrace)).ShouldBeNull();
    }

    [Fact]
    public async Task Pipeline_ShouldDeliverACancelledEmailAndTheResponseTokenMustCompleteRecovery_WhenCancelCommits()
    {
        // Arrange: a scheduled deletion whose cancel link already arrived through the broker
        (RegisterRequest request, IdentityId identityId) = await UserFactory.RegisterRandomUserWithRequestAsync(
            _apiClient, _faker, _confirmationEmailSpy, TestPassword);
        string accessToken = await GetAccessTokenAsync(request.Email);
        (await SendDeleteRequestAsync(accessToken)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _deletionEmailSpy.WaitForScheduledCaptureAsync(request.Email, DeliveryTimeout);

        // Act: cancelling is the second user-facing trigger; the courtesy notice rides the
        // pipeline while the forced-reset token travels in the response (ADR-0038)
        HttpResponseMessage cancelResponse = await _apiClient.Http.PostAsJsonAsync(
            new Uri("auth/account/cancel-deletion", UriKind.Relative),
            new CancelAccountDeletionRequest(request.Email, _deletionEmailSpy.LastCancelTokenSentTo(request.Email)!));
        await _deletionEmailSpy.WaitForCancelledCaptureAsync(request.Email, DeliveryTimeout);

        // Assert: the courtesy notice went out once, to the registered address
        cancelResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        _deletionEmailSpy.CancelledEmailCountTo(request.Email).ShouldBe(1);

        // The reset token in the response really finishes the recovery: a new password and a working login.
        CancelAccountDeletionResponse cancelBody = (await cancelResponse.Content
            .ReadFromJsonAsync<CancelAccountDeletionResponse>(_apiClient.JsonOptions))!;
        cancelBody.PasswordResetToken.ShouldNotBeNullOrWhiteSpace();
        const string newPassword = "BrandNewPass1!";
        HttpResponseMessage resetResponse = await _apiClient.Http.PostAsJsonAsync(
            new Uri("auth/reset-password", UriKind.Relative),
            new ResetPasswordRequest(request.Email, cancelBody.PasswordResetToken, newPassword));
        resetResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RequestTokenAsync(request.Email, newPassword)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The outbox row carries the user id and nothing else, and above all not the reset token from
        // the response, which must never sit in an outbox row (ADR-0038 decision 2).
        OutboxMessage? outboxRow = await OutboxAssertions.WaitForOutboxRowAsync(
            _factory,
            row => row.Type == nameof(AccountDeletionCancelled) && row.ProcessedOn != null,
            DeliveryTimeout);
        outboxRow.ShouldNotBeNull();
        AccountDeletionCancelled payload = JsonSerializer.Deserialize<AccountDeletionCancelled>(outboxRow.Payload)
            .ShouldNotBeNull();
        payload.IdentityUserId.ShouldBe(identityId.Value);
        outboxRow.Payload.ShouldNotContain(cancelBody.PasswordResetToken);

        // The delivery was recorded for deduplication and nothing was left on the broker
        (await OutboxAssertions.CountInboxRowsAsync(_factory, outboxRow.Id)).ShouldBe(1);
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailDeadLetterQueue, EmptyQueueGrace)).ShouldBeNull();
    }

    [Fact]
    public async Task Consumer_ShouldAckWithoutEmailAndWithoutParking_WhenTheScheduleIsGoneAtDelivery()
    {
        // Arrange: a user who is not deletion-scheduled: the deterministic construction of "the
        // cancellation raced the scheduled message and won" (the drift guard's reason to exist)
        (RegisterRequest request, IdentityId identityId) = await UserFactory.RegisterRandomUserWithRequestAsync(
            _apiClient, _faker, _confirmationEmailSpy, TestPassword);

        // Act: the exact wire shape a stale scheduled message would have
        Guid messageId = Guid.CreateVersion7();
        IMessagePublisher publisher = _factory.Services.GetRequiredService<IMessagePublisher>();
        await publisher.PublishAsync(
            RabbitMqTopology.EmailsExchange,
            RabbitMqTopology.DeletionScheduledRoutingKey,
            nameof(AccountDeletionScheduled),
            JsonSerializer.Serialize(new AccountDeletionScheduled(identityId.Value)),
            messageId,
            CancellationToken.None);

        // Assert: acked and recorded, but no stale "your account will be deleted" went out
        (await OutboxAssertions.WaitForInboxRowsAsync(_factory, messageId, DeliveryTimeout)).ShouldBe(1);
        _deletionEmailSpy.ScheduledEmailsTo(request.Email).ShouldBeEmpty();
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailQueue, EmptyQueueGrace)).ShouldBeNull();
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailDeadLetterQueue, EmptyQueueGrace)).ShouldBeNull();
    }

    [Fact]
    public async Task Consumer_ShouldAckWithoutEmailAndWithoutParking_WhenDeletionIsScheduledAgainAtDelivery()
    {
        // Arrange: a user who does have a deletion scheduled. A "your account was kept" notice delivered
        // after a deletion was scheduled again would say the opposite of the truth.
        (RegisterRequest request, IdentityId identityId) = await UserFactory.RegisterRandomUserWithRequestAsync(
            _apiClient, _faker, _confirmationEmailSpy, TestPassword);
        string accessToken = await GetAccessTokenAsync(request.Email);
        (await SendDeleteRequestAsync(accessToken)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _deletionEmailSpy.WaitForScheduledCaptureAsync(request.Email, DeliveryTimeout);

        // Act
        Guid messageId = Guid.CreateVersion7();
        IMessagePublisher publisher = _factory.Services.GetRequiredService<IMessagePublisher>();
        await publisher.PublishAsync(
            RabbitMqTopology.EmailsExchange,
            RabbitMqTopology.DeletionCancelledRoutingKey,
            nameof(AccountDeletionCancelled),
            JsonSerializer.Serialize(new AccountDeletionCancelled(identityId.Value)),
            messageId,
            CancellationToken.None);

        // Assert: acked and recorded, but no "your account was kept" went out
        (await OutboxAssertions.WaitForInboxRowsAsync(_factory, messageId, DeliveryTimeout)).ShouldBe(1);
        _deletionEmailSpy.CancelledEmailCountTo(request.Email).ShouldBe(0);
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailQueue, EmptyQueueGrace)).ShouldBeNull();
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailDeadLetterQueue, EmptyQueueGrace)).ShouldBeNull();
    }

    [Theory]
    [InlineData(nameof(AccountDeletionScheduled), RabbitMqTopology.DeletionScheduledRoutingKey)]
    [InlineData(nameof(AccountDeletionCancelled), RabbitMqTopology.DeletionCancelledRoutingKey)]
    public async Task Consumer_ShouldParkDeliveryInDeadLetterQueue_WhenThePayloadIsPoison(
        string messageType,
        string routingKey)
    {
        // Act: a valid message id and a registered deletion type, so the reject decision can
        // only come from the payload
        const string poisonPayload = """{"IdentityUserId":"not-a-guid"}""";
        Guid messageId = Guid.CreateVersion7();
        IMessagePublisher publisher = _factory.Services.GetRequiredService<IMessagePublisher>();
        await publisher.PublishAsync(
            RabbitMqTopology.EmailsExchange,
            routingKey,
            messageType,
            poisonPayload,
            messageId,
            CancellationToken.None);

        // Assert: parked on first sight, no e-mail, no dedup record
        BasicGetResult dead =
            (await GetWithinTimeoutAsync(RabbitMqTopology.EmailDeadLetterQueue, DeliveryTimeout)).ShouldNotBeNull();
        Encoding.UTF8.GetString(dead.Body.ToArray()).ShouldBe(poisonPayload);
        FirstDeathReason(dead.BasicProperties).ShouldBe("rejected");
        _deletionEmailSpy.ScheduledEmailCountToAnyInbox.ShouldBe(0);
        _deletionEmailSpy.CancelledEmailCountToAnyInbox.ShouldBe(0);
        (await OutboxAssertions.CountInboxRowsAsync(_factory, messageId)).ShouldBe(0);
    }

    private async Task<HttpResponseMessage> SendDeleteRequestAsync(string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "auth/account/delete");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new DeleteAccountRequest(TestPassword));

        return await _apiClient.Http.SendAsync(request);
    }

    private async Task<string> GetAccessTokenAsync(string email)
    {
        HttpResponseMessage tokenResponse = await RequestTokenAsync(email, TestPassword);
        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string content = await tokenResponse.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task<HttpResponseMessage> RequestTokenAsync(string email, string password)
    {
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = email,
            ["password"] = password,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "email profile roles api"
        });

        return await _apiClient.Http.PostAsync(new Uri("connect/token", UriKind.Relative), tokenRequest);
    }

    /// <summary>
    /// Routing and dead-lettering are asynchronous in the broker, so this polls. The read acknowledges the
    /// message, which every caller is fine with.
    /// </summary>
    private async Task<BasicGetResult?> GetWithinTimeoutAsync(string queue, TimeSpan timeout)
    {
        await using IConnection connection = await _factory.Broker.ConnectAsync(CancellationToken.None);
        await using IChannel channel = await connection.CreateChannelAsync();

        Stopwatch elapsed = Stopwatch.StartNew();

        while (true)
        {
            BasicGetResult? delivery = await channel.BasicGetAsync(queue, autoAck: true);
            if (delivery is not null)
            {
                return delivery;
            }

            if (elapsed.Elapsed >= timeout)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>
    /// Reads the reason of the first (most recent) <c>x-death</c> entry the broker stamped on a
    /// dead-lettered message.
    /// </summary>
    private static string? FirstDeathReason(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers is not { } headers
            || !headers.TryGetValue("x-death", out object? deathsRaw)
            || deathsRaw is not IList { Count: > 0 } deaths
            || deaths[0] is not IDictionary firstDeath
            || !firstDeath.Contains("reason"))
        {
            return null;
        }

        return firstDeath["reason"] is byte[] reason ? Encoding.UTF8.GetString(reason) : null;
    }
}
