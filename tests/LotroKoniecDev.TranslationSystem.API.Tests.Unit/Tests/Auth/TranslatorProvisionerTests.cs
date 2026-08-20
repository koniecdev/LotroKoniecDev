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

    // ITranslatorRepository / IUnitOfWork are genuine public boundaries (stubbed); the current-user
    // accessor is an internal interface NSubstitute can't proxy, so it gets a hand-written double.
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
        // Arrange: an authenticated token carrying no display-name material; the VO must reject it.
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
        // Arrange: only the email claim is missing; the name claim still yields a valid display name.
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
        // Arrange: a present-but-invalid email claim is non-essential to attribution: it is discarded
        // (the write still proceeds), rather than failing the whole provisioning.
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
        // Arrange: a renamed account: the existing row converges on the latest claims, no new row.
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
        // Arrange: the row already carries exactly the current claims. Provisioning now runs on every
        // authenticated request (ADR-0004 amendment), so an unchanged re-touch must be a pure read: no
        // refresh write on the hot path.
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
        // Arrange: another request inserted the row between our read and save: the unique index
        // rejects the duplicate (DbUpdateException), and we must re-read the committed row, not fail.
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

        // Assert: the committed row's id is returned and it converged on the current claims; the
        // rejected insert is dropped from tracking so it cannot re-fire on the shared unit of work.
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
        // Arrange: a DbUpdateException that is NOT a duplicate-row race (e.g. a real DB error): with
        // no committed row to re-read, the exception must propagate, never be swallowed.
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
        // Arrange: the row already carries the current claims. The first call warms the cache; the
        // second must resolve from L1 memory without touching the Translators table (PERF-07 steady
        // state), the behaviour the acceptance criterion pins.
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

        // Assert: both resolve the same id and the second call skipped the DB entirely: the Translators
        // lookup ran exactly once across the two authenticated requests. Nothing observable in the
        // returned id distinguishes a cache hit from a re-query, so the call count is the only proof.
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
        // Arrange: one identity, one process-wide cache, two requests. The existing row starts on the
        // first request's exact claims (so that request only warms the cache), then a later request
        // changes the display name, the email, or both. Two provisioners sharing a cache model two
        // scoped requests over the singleton HybridCache; the existing row is returned on every lookup.
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

        // Act: the first request warms the cache with the first fingerprint; the second presents a
        // changed fingerprint, so the cached value is bypassed and the profile refreshed.
        await firstRequest.ProvisionCurrentAsync(CancellationToken.None);
        Result<TranslatorId> afterChange = await secondRequest.ProvisionCurrentAsync(CancellationToken.None);

        // Assert: the profile converged on the latest claims (existing behaviour preserved). Had the
        // stale entry been served the refresh would never have run, so the converged state is itself the
        // proof the changed fingerprint bypassed the cache — whether the name, the email, or both moved.
        afterChange.IsSuccess.ShouldBeTrue();
        existing.DisplayName.Value.ShouldBe(secondName);
        existing.Email.ShouldNotBeNull();
        existing.Email.Value.ShouldBe(secondEmail);
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenFirstResolutionThrows_ShouldNotCacheAndRetryOnNextCall()
    {
        // Arrange: the first call fails with a non-race DB fault (no committed row to fall back on),
        // so it throws and must cache nothing; the second call with identical claims must resolve live.
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.None);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => throw new DbUpdateException("transient failure"), _ => Task.FromResult(1));
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor());

        // Act: the first call bubbles the fault; the second succeeds against the live row.
        await Should.ThrowAsync<DbUpdateException>(
            async () => await provisioner.ProvisionCurrentAsync(CancellationToken.None));
        Result<TranslatorId> second = await provisioner.ProvisionCurrentAsync(CancellationToken.None);

        // Assert: the failure was never cached: the second call re-ran the full resolution (a fresh
        // insert attempt), proving no cached entry short-circuited it.
        second.IsSuccess.ShouldBeTrue();
        _translatorRepository.Received(2).Insert(Arg.Any<Translator>());
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenConcurrentRequestsRaceOnAColdCache_ShouldShareOneResolution()
    {
        // Arrange: two scoped requests of the same identity hit a cold shared cache concurrently:
        // HybridCache's stampede protection must funnel both through ONE authoritative resolution.
        // The repository blocks inside the factory until the gate opens, pinning the overlap.
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

        // Act: the first request enters the factory and blocks; the second arrives while it is in
        // flight; then the gate releases the shared resolution for both.
        Task<Result<TranslatorId>> first = firstRequest.ProvisionCurrentAsync(CancellationToken.None).AsTask();
        await resolutionStarted.Task;
        Task<Result<TranslatorId>> second = secondRequest.ProvisionCurrentAsync(CancellationToken.None).AsTask();
        resolutionGate.SetResult();
        Result<TranslatorId> firstResult = await first;
        Result<TranslatorId> secondResult = await second;

        // Assert: both callers resolve the same id off a single Translators lookup; nothing in the
        // returned ids distinguishes a shared factory from two racing ones, so the call count is the
        // only proof the resolution was deduplicated.
        firstResult.IsSuccess.ShouldBeTrue();
        secondResult.IsSuccess.ShouldBeTrue();
        firstResult.Value.ShouldBe(existing.Id);
        secondResult.Value.ShouldBe(existing.Id);
        await _translatorRepository.Received(1).GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenInitiatingRequestAbortsWhileAnotherIsJoined_ShouldResolveTheSurvivor()
    {
        // Arrange: the #435 scenario: the request that started the shared factory aborts while a
        // second request of the same identity is joined to it. The resolution must not depend on
        // anything the aborted request owned, so the survivor still gets the id, never a fault.
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

        // Act: the initiating request enters the factory and blocks, the survivor joins the
        // in-flight resolution, then the initiator aborts BEFORE the gate opens: its own call
        // surfaces the cancellation while the shared factory keeps running for the survivor.
        Task<Result<TranslatorId>> initiating =
            firstRequest.ProvisionCurrentAsync(initiatingRequestAborted.Token).AsTask();
        await resolutionStarted.Task;
        Task<Result<TranslatorId>> survivor = secondRequest.ProvisionCurrentAsync(CancellationToken.None).AsTask();
        initiatingRequestAborted.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await initiating);
        resolutionGate.SetResult();
        Result<TranslatorId> survivorResult = await survivor;

        // Assert: the survivor resolves off the single shared lookup the aborted initiator started:
        // the call count proves it consumed that factory's result rather than re-running its own.
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
