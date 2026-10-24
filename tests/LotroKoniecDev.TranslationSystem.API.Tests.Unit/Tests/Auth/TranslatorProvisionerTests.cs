using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Auth.Provisioning;
using LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Auth;

public sealed class TranslatorProvisionerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 14, 0, 0, 0, TimeSpan.Zero);
    private static readonly IdentityId Identity = IdentityId.Create();

    // The current-user accessor is internal, which NSubstitute cannot proxy, so it gets a hand-written
    // double.
    private readonly ITranslatorRepository _translatorRepository = Substitute.For<ITranslatorRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private static Translator Existing(string displayName = "Strider")
        => Translator.Create(Identity, DisplayName.Create(displayName).Value, email: null, Now).Value;

    private TranslatorProvisioner CreateProvisioner(StubCurrentUserAccessor accessor)
        => CreateProvisioner(accessor, TestHybridCache.Create());

    private TranslatorProvisioner CreateProvisioner(StubCurrentUserAccessor accessor, HybridCache hybridCache)
        => new(
            accessor,
            TestWriteScopeFactory.Create(_translatorRepository, _unitOfWork),
            new FixedTimeProvider(Now),
            hybridCache);

    private static StubCurrentUserAccessor Accessor(
        ValueMaybe<IdentityId>? identity = null,
        string? username = "Aragorn",
        string? email = "aragorn@gondor.test")
        => new(identity ?? ValueMaybe<IdentityId>.From(Identity), username, email);

    [Fact]
    public async Task ProvisionCurrentAsync_WhenNoIdentity_ShouldReturnForbiddenAndNotPersist()
    {
        // Arrange: a token without a parseable subject must never be attributed.
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor(ValueMaybe<IdentityId>.None()));

        // Act
        Result<TranslatorId> result = await provisioner.ProvisionCurrentAsync(CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translators.Unauthenticated");
        _translatorRepository.DidNotReceive().Insert(Arg.Any<Translator>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenNeitherNameNorEmailClaim_ShouldFailValidationAndNotPersist()
    {
        // Arrange
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor(username: null, email: null));
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.None);

        // Act
        Result<TranslatorId> result = await provisioner.ProvisionCurrentAsync(CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslatorEntity.DisplayName.NullOrEmpty");
        _translatorRepository.DidNotReceive().Insert(Arg.Any<Translator>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenNoEmailClaim_ShouldFallBackToEmaillessProfile()
    {
        // Arrange
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor(email: null));
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.None);
        Translator? inserted = null;
        _translatorRepository.When(repository => repository.Insert(Arg.Any<Translator>()))
            .Do(callInfo => inserted = callInfo.Arg<Translator>());

        // Act
        Result<TranslatorId> result = await provisioner.ProvisionCurrentAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        inserted.ShouldNotBeNull();
        inserted.DisplayName.Value.ShouldBe("Aragorn");
        inserted.Email.ShouldBeNull();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenEmailClaimMalformed_ShouldDiscardItAndProvisionEmaillessProfile()
    {
        // Arrange: the e-mail is not needed for attribution, so a bad one must not fail the write.
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor(email: "not-an-email"));
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.None);
        Translator? inserted = null;
        _translatorRepository.When(repository => repository.Insert(Arg.Any<Translator>()))
            .Do(callInfo => inserted = callInfo.Arg<Translator>());

        // Act
        Result<TranslatorId> result = await provisioner.ProvisionCurrentAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        inserted.ShouldNotBeNull();
        inserted.DisplayName.Value.ShouldBe("Aragorn");
        inserted.Email.ShouldBeNull();
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenAlreadyProvisioned_ShouldRefreshAndReturnExistingIdWithoutInsert()
    {
        // Arrange: a renamed account.
        Translator existing = Existing("Strider");
        TranslatorId existingId = existing.Id;
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.From(existing));
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor());

        // Act
        Result<TranslatorId> result = await provisioner.ProvisionCurrentAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(existingId);
        existing.DisplayName.Value.ShouldBe("Aragorn");
        _translatorRepository.DidNotReceive().Insert(Arg.Any<Translator>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenAlreadyProvisionedAndClaimsUnchanged_ShouldReturnExistingIdWithoutWriting()
    {
        // Arrange: provisioning runs on every authenticated request (ADR-0004 amendment), so unchanged
        // claims must cost no write.
        Translator existing = Translator.Create(
            Identity,
            DisplayName.Create("Aragorn").Value,
            Email.Create("aragorn@gondor.test").Value,
            Now).Value;
        TranslatorId existingId = existing.Id;
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.From(existing));
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor());

        // Act
        Result<TranslatorId> result = await provisioner.ProvisionCurrentAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(existingId);
        _translatorRepository.DidNotReceive().Insert(Arg.Any<Translator>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenNew_ShouldInsertAndReturnNewId()
    {
        // Arrange
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.None);
        Translator? inserted = null;
        _translatorRepository.When(repository => repository.Insert(Arg.Any<Translator>()))
            .Do(callInfo => inserted = callInfo.Arg<Translator>());
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor());

        // Act
        Result<TranslatorId> result = await provisioner.ProvisionCurrentAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        inserted.ShouldNotBeNull();
        inserted.IdentityId.ShouldBe(Identity);
        result.Value.ShouldBe(inserted.Id);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenConcurrentFirstWriteRaces_ShouldReReadCommittedRowAndSucceed()
    {
        // Arrange: another request inserted the row between our read and save.
        Translator raced = Existing("Strider");
        TranslatorId racedId = raced.Id;
        Translator? rejectedInsert = null;
        _translatorRepository.When(repository => repository.Insert(Arg.Any<Translator>()))
            .Do(call => rejectedInsert = call.Arg<Translator>());
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.None, Maybe<Translator>.From(raced));
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => throw new DbUpdateException("unique violation"), _ => Task.FromResult(1));
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor());

        // Act
        Result<TranslatorId> result = await provisioner.ProvisionCurrentAsync(CancellationToken.None);

        // Assert: the rejected insert is detached, so it cannot fire again on the next save.
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(racedId);
        raced.DisplayName.Value.ShouldBe("Aragorn");
        rejectedInsert.ShouldNotBeNull();
        _translatorRepository.Received(1).Detach(rejectedInsert);
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenSaveFailsAndRowStillMissing_ShouldRethrow()
    {
        // Arrange: a real database error, not a race: there is no committed row to re-read.
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.None, Maybe<Translator>.None);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateException("transient failure"));
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor());

        // Act + Assert
        await Should.ThrowAsync<DbUpdateException>(
            async () => await provisioner.ProvisionCurrentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenCalledTwiceWithUnchangedClaims_ShouldQueryTranslatorsOnlyOnce()
    {
        // Arrange: the first call warms the cache; the second must not touch the table (PERF-07).
        Translator existing = Translator.Create(
            Identity,
            DisplayName.Create("Aragorn").Value,
            Email.Create("aragorn@gondor.test").Value,
            Now).Value;
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.From(existing));
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor());

        // Act
        Result<TranslatorId> first = await provisioner.ProvisionCurrentAsync(CancellationToken.None);
        Result<TranslatorId> second = await provisioner.ProvisionCurrentAsync(CancellationToken.None);

        // Assert: the id cannot tell a cache hit from a re-query, so the call count is the only proof.
        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        second.Value.ShouldBe(first.Value);
        await _translatorRepository.Received(1).GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Strider", "ranger@rivendell.test", "Aragorn", "ranger@rivendell.test")] // display name only
    [InlineData("Aragorn", "before@gondor.test", "Aragorn", "after@gondor.test")]        // email only
    [InlineData("Strider", "strider@rangers.test", "Aragorn", "aragorn@gondor.test")]    // both
    public async Task ProvisionCurrentAsync_WhenAClaimChangesBetweenRequests_ShouldBypassCacheAndRefreshProfile(
        string firstName, string firstEmail, string secondName, string secondEmail)
    {
        // Arrange: two provisioners sharing one cache model two requests. The first only warms the
        // cache; the second changes the name, the e-mail or both.
        Translator existing = Translator.Create(
            Identity,
            DisplayName.Create(firstName).Value,
            Email.Create(firstEmail).Value,
            Now).Value;
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.From(existing));
        HybridCache sharedCache = TestHybridCache.Create();
        TranslatorProvisioner firstRequest =
            CreateProvisioner(Accessor(username: firstName, email: firstEmail), sharedCache);
        TranslatorProvisioner secondRequest =
            CreateProvisioner(Accessor(username: secondName, email: secondEmail), sharedCache);

        // Act
        await firstRequest.ProvisionCurrentAsync(CancellationToken.None);
        Result<TranslatorId> afterChange = await secondRequest.ProvisionCurrentAsync(CancellationToken.None);

        // Assert: had the old entry been used, the refresh would never have run.
        afterChange.IsSuccess.ShouldBeTrue();
        existing.DisplayName.Value.ShouldBe(secondName);
        existing.Email.ShouldNotBeNull();
        existing.Email.Value.ShouldBe(secondEmail);
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenFirstResolutionThrows_ShouldNotCacheAndRetryOnNextCall()
    {
        // Arrange: the first call fails with a real database fault.
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.None);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => throw new DbUpdateException("transient failure"), _ => Task.FromResult(1));
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor());

        // Act
        await Should.ThrowAsync<DbUpdateException>(
            async () => await provisioner.ProvisionCurrentAsync(CancellationToken.None));
        Result<TranslatorId> second = await provisioner.ProvisionCurrentAsync(CancellationToken.None);

        // Assert: a second insert attempt proves no cached entry short-circuited the call.
        second.IsSuccess.ShouldBeTrue();
        _translatorRepository.Received(2).Insert(Arg.Any<Translator>());
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenConcurrentRequestsRaceOnAColdCache_ShouldShareOneResolution()
    {
        // Arrange: the repository blocks inside the factory until the gate opens, so the two requests
        // overlap for sure.
        Translator existing = Translator.Create(
            Identity,
            DisplayName.Create("Aragorn").Value,
            Email.Create("aragorn@gondor.test").Value,
            Now).Value;
        TaskCompletionSource resolutionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resolutionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                resolutionStarted.TrySetResult();
                await resolutionGate.Task;
                return Maybe<Translator>.From(existing);
            });
        HybridCache sharedCache = TestHybridCache.Create();
        TranslatorProvisioner firstRequest = CreateProvisioner(Accessor(), sharedCache);
        TranslatorProvisioner secondRequest = CreateProvisioner(Accessor(), sharedCache);

        // Act
        Task<Result<TranslatorId>> first = firstRequest.ProvisionCurrentAsync(CancellationToken.None).AsTask();
        await resolutionStarted.Task;
        Task<Result<TranslatorId>> second = secondRequest.ProvisionCurrentAsync(CancellationToken.None).AsTask();
        resolutionGate.SetResult();
        Result<TranslatorId> firstResult = await first;
        Result<TranslatorId> secondResult = await second;

        // Assert: the ids cannot tell one shared factory from two racing ones, so the call count is the
        // only proof.
        firstResult.IsSuccess.ShouldBeTrue();
        secondResult.IsSuccess.ShouldBeTrue();
        firstResult.Value.ShouldBe(existing.Id);
        secondResult.Value.ShouldBe(existing.Id);
        await _translatorRepository.Received(1).GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenInitiatingRequestAbortsWhileAnotherIsJoined_ShouldResolveTheSurvivor()
    {
        // Arrange: #435. The resolution must not depend on anything the aborted request owned.
        Translator existing = Translator.Create(
            Identity,
            DisplayName.Create("Aragorn").Value,
            Email.Create("aragorn@gondor.test").Value,
            Now).Value;
        TaskCompletionSource resolutionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resolutionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                resolutionStarted.TrySetResult();
                await resolutionGate.Task;
                return Maybe<Translator>.From(existing);
            });
        HybridCache sharedCache = TestHybridCache.Create();
        TranslatorProvisioner firstRequest = CreateProvisioner(Accessor(), sharedCache);
        TranslatorProvisioner secondRequest = CreateProvisioner(Accessor(), sharedCache);
        using CancellationTokenSource initiatingRequestAborted = new();

        // Act: the initiator aborts before the gate opens, while the survivor is joined.
        Task<Result<TranslatorId>> initiating =
            firstRequest.ProvisionCurrentAsync(initiatingRequestAborted.Token).AsTask();
        await resolutionStarted.Task;
        Task<Result<TranslatorId>> survivor = secondRequest.ProvisionCurrentAsync(CancellationToken.None).AsTask();
        initiatingRequestAborted.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await initiating);
        resolutionGate.SetResult();
        Result<TranslatorId> survivorResult = await survivor;

        // Assert: one lookup, so the survivor used the shared factory's result.
        survivorResult.IsSuccess.ShouldBeTrue();
        survivorResult.Value.ShouldBe(existing.Id);
        await _translatorRepository.Received(1).GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>());
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
