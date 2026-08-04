using System.Collections;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using RabbitMQ.Client;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Messaging;

/// <summary>
/// The password-reset legs of the brokered pipeline (MSG-03, ADR-0038): forgot-password commits
/// an id-only outbox row, the real relay publishes it to a real broker, and the real consumer
/// selects <c>PasswordResetRequestProcessor</c> by the AMQP <c>type</c> property, mints the token
/// at delivery and dispatches to the spy sender. The legs mirror
/// <see cref="EmailConfirmationPipelineTests"/> where the machinery is shared and add what is
/// specific to this type: the token must never appear in an outbox row or a parked message, and
/// the delivered token must actually reset the password.
/// </summary>
public sealed class PasswordResetPipelineTests : IClassFixture<BrokeredAuthSystemApiFactory>, IAsyncLifetime
{
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan EmptyQueueGrace = TimeSpan.FromSeconds(2);

    private readonly BrokeredAuthSystemApiFactory _factory;
    private readonly TestApiClient _apiClient;
    private readonly SpyAccountConfirmationEmailSender _confirmationEmailSpy;
    private readonly SpyPasswordResetEmailSender _passwordResetEmailSpy;
    private readonly Bogus.Faker _faker = new();

    public PasswordResetPipelineTests(BrokeredAuthSystemApiFactory factory)
    {
        _factory = factory;
        _confirmationEmailSpy = factory.Services.GetRequiredService<SpyAccountConfirmationEmailSender>();
        _passwordResetEmailSpy = factory.Services.GetRequiredService<SpyPasswordResetEmailSender>();

        JsonSerializerOptions jsonSerializerOptions =
            factory.Services.GetRequiredService<IOptionsSnapshot<JsonOptions>>().Value.SerializerOptions;
        _apiClient = new TestApiClient(factory.CreateClient(), jsonSerializerOptions);
    }

    public async Task InitializeAsync()
    {
        _confirmationEmailSpy.Reset();
        _passwordResetEmailSpy.Reset();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        CleanerService cleaner = scope.ServiceProvider.GetRequiredService<CleanerService>();
        await cleaner.CleanAsync();

        // The host's consumer declares the topology on startup, but the first test may reach this
        // point before that attach finished — declaring here is idempotent (proven in
        // DeadLetterTopologyTests) and guarantees the purges below have queues to purge.
        await using IConnection connection = await _factory.Broker.ConnectAsync(CancellationToken.None);
        await using IChannel channel = await connection.CreateChannelAsync();
        await RabbitMqTopologyDeclaration.DeclareAsync(channel, CancellationToken.None);
        await channel.QueuePurgeAsync(RabbitMqTopology.EmailQueue);
        await channel.QueuePurgeAsync(RabbitMqTopology.EmailDeadLetterQueue);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Pipeline_ShouldDeliverAResetEmailWhoseTokenWorks_WhenForgotPasswordCommits()
    {
        // Arrange — a confirmed account, so the only pipeline traffic left is the reset e-mail
        (RegisterRequest request, IdentityId identityId) = await UserFactory.RegisterRandomUserWithRequestAsync(
            _apiClient, _faker, _confirmationEmailSpy);

        // Act — forgot-password is the user-facing trigger of the whole pipeline
        HttpResponseMessage forgotResponse = await _apiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative), new ForgotPasswordRequest(request.Email));
        await _passwordResetEmailSpy.WaitForCaptureAsync(DeliveryTimeout);

        // Assert — the e-mail went out once, to the registered address, with a usable token
        forgotResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        _passwordResetEmailSpy.LastEmail.ShouldBe(request.Email);
        _passwordResetEmailSpy.LastResetToken.ShouldNotBeNullOrWhiteSpace();
        _passwordResetEmailSpy.CallCount.ShouldBe(1);

        // The delivered token really resets the password — the end-to-end proof that minting at
        // delivery produced a link the user can use
        HttpResponseMessage resetResponse = await _apiClient.Http.PostAsJsonAsync(
            new Uri("auth/reset-password", UriKind.Relative),
            new ResetPasswordRequest(request.Email, _passwordResetEmailSpy.LastResetToken!, "NewPass99!"));
        resetResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The outbox row was published and marked on the first attempt — and carries the user id
        // alone: no reset token may ever persist in an outbox row (ADR-0038 decision 2)
        OutboxMessage? outboxRow = await OutboxAssertions.WaitForOutboxRowAsync(
            _factory,
            row => row.Type == nameof(PasswordResetRequested) && row.ProcessedOn != null,
            DeliveryTimeout);
        outboxRow.ShouldNotBeNull();
        outboxRow.Attempts.ShouldBe(0);
        outboxRow.LastError.ShouldBeNull();
        PasswordResetRequested payload = JsonSerializer.Deserialize<PasswordResetRequested>(outboxRow.Payload)
            .ShouldNotBeNull();
        payload.IdentityUserId.ShouldBe(identityId.Value);
        outboxRow.Payload.ShouldNotContain(_passwordResetEmailSpy.LastResetToken!);

        // The delivery was recorded for deduplication and nothing was left on the broker
        (await OutboxAssertions.CountInboxRowsAsync(_factory, outboxRow.Id)).ShouldBe(1);
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailQueue, EmptyQueueGrace)).ShouldBeNull();
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailDeadLetterQueue, EmptyQueueGrace)).ShouldBeNull();
    }

    [Fact]
    public async Task Consumer_ShouldAckWithoutSecondEmail_WhenTheSameMessageIdIsDeliveredAgain()
    {
        // Arrange — a fully delivered reset request, then its exact wire message published again
        // (the relay's crash window: published but not yet marked processed → re-published later)
        (RegisterRequest request, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            _apiClient, _faker, _confirmationEmailSpy);
        await _apiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative), new ForgotPasswordRequest(request.Email));
        await _passwordResetEmailSpy.WaitForCaptureAsync(DeliveryTimeout);
        OutboxMessage? outboxRow = await OutboxAssertions.WaitForOutboxRowAsync(
            _factory,
            row => row.Type == nameof(PasswordResetRequested) && row.ProcessedOn != null,
            DeliveryTimeout);
        outboxRow.ShouldNotBeNull();
        int sendsAfterFirstDelivery = _passwordResetEmailSpy.CallCount;

        // Act — same payload, same type, same message id, over the real wire
        IMessagePublisher publisher = _factory.Services.GetRequiredService<IMessagePublisher>();
        await publisher.PublishAsync(
            RabbitMqTopology.PasswordResetRoutingKey,
            outboxRow.Type,
            outboxRow.Payload,
            outboxRow.Id,
            CancellationToken.None);

        // Assert — the duplicate is consumed (queue drains), acked (no parking) and suppressed
        await WaitUntilQueueEmptyAsync(RabbitMqTopology.EmailQueue, DeliveryTimeout);
        await Task.Delay(TimeSpan.FromSeconds(1));
        _passwordResetEmailSpy.CallCount.ShouldBe(sendsAfterFirstDelivery);
        (await OutboxAssertions.CountInboxRowsAsync(_factory, outboxRow.Id)).ShouldBe(1);
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailDeadLetterQueue, EmptyQueueGrace)).ShouldBeNull();
    }

    [Theory]
    [InlineData("this is not json at all")]
    [InlineData("{}")]
    [InlineData("""{"IdentityUserId":"not-a-guid"}""")]
    public async Task Consumer_ShouldParkDeliveryInDeadLetterQueue_WhenThePayloadIsPoison(string poisonPayload)
    {
        // Act — a valid message id and the registered reset type, so the reject decision can only
        // come from the payload
        Guid messageId = Guid.CreateVersion7();
        IMessagePublisher publisher = _factory.Services.GetRequiredService<IMessagePublisher>();
        await publisher.PublishAsync(
            RabbitMqTopology.PasswordResetRoutingKey,
            nameof(PasswordResetRequested),
            poisonPayload,
            messageId,
            CancellationToken.None);

        // Assert — parked on first sight, no e-mail, no dedup record
        BasicGetResult dead =
            (await GetWithinTimeoutAsync(RabbitMqTopology.EmailDeadLetterQueue, DeliveryTimeout)).ShouldNotBeNull();
        Encoding.UTF8.GetString(dead.Body.ToArray()).ShouldBe(poisonPayload);
        FirstDeathReason(dead.BasicProperties).ShouldBe("rejected");
        _passwordResetEmailSpy.CallCount.ShouldBe(0);
        (await OutboxAssertions.CountInboxRowsAsync(_factory, messageId)).ShouldBe(0);
    }

    [Fact]
    public async Task Consumer_ShouldAckWithoutEmailAndWithoutParking_WhenTheUserNoLongerExists()
    {
        // Act — a payload whose user id matches no account (requested, then erased): redelivery
        // could never change the outcome, so the ack-and-record contract applies, not the DLQ
        Guid messageId = Guid.CreateVersion7();
        string payload = JsonSerializer.Serialize(new PasswordResetRequested(Guid.CreateVersion7()));
        IMessagePublisher publisher = _factory.Services.GetRequiredService<IMessagePublisher>();
        await publisher.PublishAsync(
            RabbitMqTopology.PasswordResetRoutingKey,
            nameof(PasswordResetRequested),
            payload,
            messageId,
            CancellationToken.None);

        // Assert — the inbox record doubles as the "consumer finished" signal
        int inboxRows = await OutboxAssertions.WaitForInboxRowsAsync(_factory, messageId, DeliveryTimeout);
        inboxRows.ShouldBe(1);
        _passwordResetEmailSpy.CallCount.ShouldBe(0);
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailQueue, EmptyQueueGrace)).ShouldBeNull();
        (await GetWithinTimeoutAsync(RabbitMqTopology.EmailDeadLetterQueue, EmptyQueueGrace)).ShouldBeNull();
    }

    /// <summary>
    /// Polls the queue until a delivery arrives or the timeout passes — routing and dead-lettering
    /// are asynchronous inside the broker. The read is auto-acked: every caller either asserts on
    /// the delivery (test over either way) or expects null.
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

    private async Task WaitUntilQueueEmptyAsync(string queue, TimeSpan timeout)
    {
        await using IConnection connection = await _factory.Broker.ConnectAsync(CancellationToken.None);
        await using IChannel channel = await connection.CreateChannelAsync();

        Stopwatch elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < timeout)
        {
            uint messageCount = await channel.MessageCountAsync(queue);
            if (messageCount == 0)
            {
                return;
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
