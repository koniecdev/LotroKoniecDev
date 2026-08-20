using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// Exercises the signal-driven relay end-to-end against the real host: registration commits an
/// outbox row and nudges the signal, the hosted relay publishes to the (spy) broker and marks the
/// row processed. No fixed poll interval exists to lean on (ADR-0035), so every test waits on the
/// observable database state, never on time.
/// </summary>
public sealed class OutboxRelayTests : EndpointsTestBase
{
    private static readonly TimeSpan RelayReactionTimeout = TimeSpan.FromSeconds(15);

    private readonly SpyMessagePublisher _messagePublisherSpy;

    public OutboxRelayTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
        _messagePublisherSpy = appFactory.Services.GetRequiredService<SpyMessagePublisher>();
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _messagePublisherSpy.Reset();
    }

    [Fact]
    public async Task Relay_ShouldPublishAndMarkProcessed_WhenRegistrationCommits()
    {
        // Arrange & Act: registration is the outbox writer under test
        (RegisterRequest _, IdentityId identityId) = await UserFactory.RegisterRandomUserUnconfirmedAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy);

        // Assert
        OutboxMessage? message = await WaitForOutboxRowAsync(
            row => row.Payload.Contains(identityId.Value.ToString(), StringComparison.OrdinalIgnoreCase)
                   && row.ProcessedOn != null);

        message.ShouldNotBeNull();
        message.IsProcessed().ShouldBeTrue();
        message.Attempts.ShouldBe(0);
        message.LastError.ShouldBeNull();

        SpyMessagePublisher.PublishedMessage published = _messagePublisherSpy.Published
            .ShouldHaveSingleItem();
        published.MessageId.ShouldBe(message.Id);
        published.RoutingKey.ShouldBe(RabbitMqTopology.EmailConfirmationRoutingKey);
        published.Type.ShouldBe(nameof(EmailConfirmationRequested));
        published.Payload.ShouldBe(message.Payload);
    }

    [Fact]
    public async Task Relay_ShouldMarkFailedAndRetainRow_WhenBrokerRefusesPublish()
    {
        // Arrange
        _messagePublisherSpy.FailWith = new InvalidOperationException("broker down");
        Guid messageId = await InsertOutboxRowAsync(nameof(EmailConfirmationRequested));

        // Act
        NotifyRelay();

        // Assert: the row survives the refusal, carrying the failure diagnostics
        OutboxMessage? failed = await WaitForOutboxRowAsync(
            row => row.Id == messageId && row.Attempts > 0);

        failed.ShouldNotBeNull();
        failed.IsProcessed().ShouldBeFalse();
        failed.LastError.ShouldBe("broker down");

        // Act again — the broker heals and a fresh nudge retries the same row
        _messagePublisherSpy.FailWith = null;
        NotifyRelay();

        OutboxMessage? recovered = await WaitForOutboxRowAsync(
            row => row.Id == messageId && row.ProcessedOn != null);

        recovered.ShouldNotBeNull();
        recovered.IsProcessed().ShouldBeTrue();
        _messagePublisherSpy.Published.ShouldContain(published => published.MessageId == messageId);
    }

    [Fact]
    public async Task Registration_ShouldCommitNoOutboxRow_WhenRegistrationFails()
    {
        // Arrange: a taken e-mail address makes the second registration fail inside the same
        // transaction that would have written its outbox row
        (RegisterRequest existingRequest, _) = await UserFactory.RegisterRandomUserUnconfirmedAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy);

        RegisterRequest duplicateRequest = new(
            Faker.Random.AlphaNumeric(16),
            existingRequest.Email,
            "TestPass1!",
            AcceptedPrivacyPolicy: true,
            AcceptedDataProcessingConsent: true,
            AcceptedTermsOfService: true);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), duplicateRequest);

        // Assert: the failure rolled back atomically: only the first registration's row exists,
        // so the pipeline can never send a confirmation e-mail for an account that was not created
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        (await db.OutboxMessages.AsNoTracking().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Relay_ShouldMarkFailedWithoutPublishing_WhenTypeHasNoRoutingKey()
    {
        // Arrange
        Guid messageId = await InsertOutboxRowAsync("TypeNobodyMapped");

        // Act
        NotifyRelay();

        // Assert
        OutboxMessage? unroutable = await WaitForOutboxRowAsync(
            row => row.Id == messageId && row.Attempts > 0);

        unroutable.ShouldNotBeNull();
        unroutable.IsProcessed().ShouldBeFalse();
        unroutable.LastError.ShouldNotBeNull();
        unroutable.LastError.ShouldContain("No routing key");
        _messagePublisherSpy.Published.ShouldBeEmpty();
    }

    private void NotifyRelay()
    {
        Factory.Services.GetRequiredService<OutboxSignal>().Notify();
    }

    private async Task<Guid> InsertOutboxRowAsync(string type)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        OutboxMessage message = OutboxMessage.Create(
            type: type,
            payload: $$"""{"UserId":"{{Guid.CreateVersion7()}}"}""",
            occurredOn: DateTimeOffset.UtcNow);

        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync();

        return message.Id;
    }

    /// <summary>
    /// Polls the database until a row matches or <see cref="RelayReactionTimeout"/> elapses, then
    /// returns the latest snapshot (or null) — the assertions on it stay in the test body.
    /// </summary>
    private async Task<OutboxMessage?> WaitForOutboxRowAsync(Func<OutboxMessage, bool> predicate)
    {
        using CancellationTokenSource timeout = new(RelayReactionTimeout);

        while (true)
        {
            await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

            List<OutboxMessage> rows = await db.OutboxMessages.AsNoTracking().ToListAsync();
            OutboxMessage? match = rows.FirstOrDefault(predicate);

            if (match is not null || timeout.IsCancellationRequested)
            {
                return match;
            }

            await Task.Delay(100);
        }
    }
}
