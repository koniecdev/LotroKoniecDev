using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// Proves the inbox deduplication of ADR-0037 against real PostgreSQL, through
/// <see cref="EmailConfirmationDeliveryProcessor"/> — the one component both real delivery paths
/// (the broker consumer and this suite's broker-less bridge) resolve: a processed message id is
/// recorded, a duplicate of it is acknowledged without a second e-mail, and a failed processing
/// leaves no record so redelivery genuinely retries.
/// </summary>
public sealed class InboxDeduplicationTests : EndpointsTestBase
{
    public InboxDeduplicationTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    [Fact]
    public async Task ProcessOnce_ShouldRecordTheMessageId_WhenProcessingSucceeds()
    {
        // Arrange
        (RegisterRequest _, IdentityId identityId) = await UserFactory.RegisterRandomUserUnconfirmedAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy);
        await AccountConfirmationEmailSpy.WaitForCaptureAsync();
        Guid messageId = Guid.CreateVersion7();
        int sendsBefore = AccountConfirmationEmailSpy.CallCount;

        // Act
        Result ackDecision = await ProcessOnceAsync(identityId.Value, messageId);

        // Assert
        ackDecision.IsSuccess.ShouldBeTrue();
        AccountConfirmationEmailSpy.CallCount.ShouldBe(sendsBefore + 1);
        (await CountInboxRowsAsync(messageId)).ShouldBe(1);
    }

    [Fact]
    public async Task ProcessOnce_ShouldAckWithoutSecondEmail_WhenMessageIdAlreadyRecorded()
    {
        // Arrange
        (RegisterRequest _, IdentityId identityId) = await UserFactory.RegisterRandomUserUnconfirmedAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy);
        await AccountConfirmationEmailSpy.WaitForCaptureAsync();
        Guid messageId = Guid.CreateVersion7();
        await ProcessOnceAsync(identityId.Value, messageId);
        int sendsAfterFirstDelivery = AccountConfirmationEmailSpy.CallCount;

        // Act — the same message id delivered again (redelivery or relay re-publish)
        Result duplicateAckDecision = await ProcessOnceAsync(identityId.Value, messageId);

        // Assert — acked, no second e-mail, still exactly one row
        duplicateAckDecision.IsSuccess.ShouldBeTrue();
        AccountConfirmationEmailSpy.CallCount.ShouldBe(sendsAfterFirstDelivery);
        (await CountInboxRowsAsync(messageId)).ShouldBe(1);
    }

    [Fact]
    public async Task ProcessOnce_ShouldLeaveNoRecord_WhenProcessingFails()
    {
        // Arrange
        (RegisterRequest _, IdentityId identityId) = await UserFactory.RegisterRandomUserUnconfirmedAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy);
        await AccountConfirmationEmailSpy.WaitForCaptureAsync();
        Guid messageId = Guid.CreateVersion7();
        AccountConfirmationEmailSpy.ShouldFail = true;

        // Act — the failed send must not be remembered as processed
        Result failedAckDecision = await ProcessOnceAsync(identityId.Value, messageId);

        // Assert
        failedAckDecision.IsFailure.ShouldBeTrue();
        (await CountInboxRowsAsync(messageId)).ShouldBe(0);

        // Act again — after the dependency heals, the redelivery really retries and records
        AccountConfirmationEmailSpy.ShouldFail = false;
        Result redeliveryAckDecision = await ProcessOnceAsync(identityId.Value, messageId);

        // Assert
        redeliveryAckDecision.IsSuccess.ShouldBeTrue();
        (await CountInboxRowsAsync(messageId)).ShouldBe(1);
    }

    private async Task<Result> ProcessOnceAsync(Guid identityUserId, Guid messageId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        EmailConfirmationDeliveryProcessor deliveryProcessor =
            scope.ServiceProvider.GetRequiredService<EmailConfirmationDeliveryProcessor>();
        return await deliveryProcessor.ProcessOnceAsync(
            new EmailConfirmationRequested(identityUserId), messageId, CancellationToken.None);
    }

    private async Task<int> CountInboxRowsAsync(Guid messageId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.InboxMessages.AsNoTracking().CountAsync(row => row.MessageId == messageId);
    }
}
