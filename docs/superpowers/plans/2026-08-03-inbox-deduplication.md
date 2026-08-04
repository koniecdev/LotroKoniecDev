# Inbox Deduplication (ADR-0037) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the e-mail consumer a durable inbox (dedup on the broker message id) so a redelivered or re-published message never sends a second confirmation e-mail — per ADR-0037 (`docs/adr/0037-consumer-side-inbox-deduplication-on-the-message-id.md`).

**Architecture:** One new table (`InboxMessages`: MessageId PK + ProcessedOn) in the auth database. The dedup orchestration (check inbox → run `EmailConfirmationRequestProcessor` → record on success) lives in one new scoped component, `EmailConfirmationDeliveryProcessor`, resolved by BOTH real delivery paths: `EmailConfirmationConsumer.OnDeliveredAsync` (production, real broker) and the integration suite's broker-less bridge in `AuthSystemApiFactory` (which today calls the processor directly and would otherwise need a drifting copy of the dedup logic). Retry/backoff/DLQ stay broker-owned (ADR-0036) — this plan never touches them.

**Tech Stack:** .NET 10 / C# 14, EF Core 10 + Npgsql (PostgreSQL), RabbitMQ.Client v7, xUnit + Shouldly, Testcontainers (integration suite).

## Global Constraints

- **Zero warnings**: `TreatWarningsAsErrors` is repo-wide — any warning IS a failing build. Verify with `dotnet build LotroKoniecDev.slnx`.
- **Branch**: work on the existing `rabbitmq-introduction` branch. Never commit to main.
- **Commit trailer** (every commit):

  ```
  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01RkmEvoRyYvnHSCiNGLfMzK
  ```

- **C# style**: `sealed` everything without explicit inheritance; explicit constructors (no primary constructors in classes); `var` only for anonymous types; file-scoped namespaces; Allman braces; no `#region`; `/// <summary>` XML docs (never plain `//` to document a member); LINQ methods, never query syntax; code and identifiers in English.
- **Errors are values**: business failures → `Result` (`LotroKoniecDev.SharedKernel.Monads`; `IsSuccess`/`IsFailure`/`Error` all exist); `Ensure.*`/`Argument*Exception.Throw*` guards are for programmer errors only.
- **EF Core**: Fluent API only, no attributes; no needless `IsRequired()` (value types are already required); migrations are forward-only and this one must be purely additive (`CREATE TABLE` only — trivially N-1 safe, no `MIGRATION-SAFETY` comment needed).
- **Tests**: xUnit + Shouldly (never raw `Assert.*`); naming `MethodName_Scenario_ExpectedResult`; AAA with assertions inline in the test method; `[Theory]`+`[InlineData]` for unhappy-path matrices. Xunit/Shouldly are global usings in both auth test projects — don't add `using Xunit;`/`using Shouldly;`.
- **Never add** `Mediator`/`MediatR` packages (ADR-0001).
- **Docker required** for the integration suite (`tests/LotroKoniecDev.AuthSystem.API.Tests.Integration` boots a PostgreSQL Testcontainer). If Docker is unavailable, say so loudly instead of skipping silently.

---

### Task 1: `InboxMessage` entity + unit tests

**Files:**
- Create: `src/AuthSystem/LotroKoniecDev.AuthSystem.Persistence/Inbox/InboxMessage.cs`
- Test: `tests/LotroKoniecDev.AuthSystem.API.Tests.Unit/Inbox/InboxMessageTests.cs`

**Interfaces:**
- Consumes: `LotroKoniecDev.SharedKernel.Guards.Ensure.NotEmpty(Guid)` and `Ensure.NotEmpty(DateTimeOffset)` — both throw `ArgumentException`.
- Produces: `public sealed class InboxMessage` with `Guid MessageId { get; }`, `DateTimeOffset ProcessedOn { get; }`, `static InboxMessage Create(Guid messageId, DateTimeOffset processedOn)`. Tasks 2 and 3 rely on exactly these names.

- [ ] **Step 1: Write the failing tests**

Create `tests/LotroKoniecDev.AuthSystem.API.Tests.Unit/Inbox/InboxMessageTests.cs`:

```csharp
using LotroKoniecDev.AuthSystem.Persistence.Inbox;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Inbox;

public sealed class InboxMessageTests
{
    [Fact]
    public void Create_WithValidArguments_SetsMessageIdAndProcessedOn()
    {
        // Arrange
        Guid messageId = Guid.CreateVersion7();
        DateTimeOffset processedOn = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

        // Act
        InboxMessage message = InboxMessage.Create(messageId, processedOn);

        // Assert
        message.MessageId.ShouldBe(messageId);
        message.ProcessedOn.ShouldBe(processedOn);
    }

    [Fact]
    public void Create_WithEmptyMessageId_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() =>
            InboxMessage.Create(Guid.Empty, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Create_WithDefaultProcessedOn_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() =>
            InboxMessage.Create(Guid.CreateVersion7(), default));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/LotroKoniecDev.AuthSystem.API.Tests.Unit --filter "FullyQualifiedName~InboxMessageTests"`
Expected: build FAILURE — `InboxMessage` and namespace `...Persistence.Inbox` do not exist yet.

- [ ] **Step 3: Write the entity**

Create `src/AuthSystem/LotroKoniecDev.AuthSystem.Persistence/Inbox/InboxMessage.cs`. Member order per house rules (properties → factory → private ctors); the parameterless ctor is for EF:

```csharp
using LotroKoniecDev.SharedKernel.Guards;

namespace LotroKoniecDev.AuthSystem.Persistence.Inbox;

/// <summary>
/// One row per fully processed broker delivery, keyed by the broker message id — which equals the
/// publishing <see cref="Outbox.OutboxMessage"/>'s Id, so the full context of any inbox row is one
/// join away. The consumer checks this table before doing any work and records into it after
/// success, so a redelivered or re-published message is acknowledged without a second side effect
/// (ADR-0037).
/// </summary>
/// <remarks>
/// Deliberately carries no Type, no Payload and no attempt counters: the outbox row with the same
/// id holds the former two, and retry bookkeeping is broker-owned (ADR-0036). One hard constraint
/// from ADR-0037: this table serves the single e-mail consumer — a second consumer of the same
/// message (a fanout binding) must NOT share it without adding a consumer discriminator, or the
/// two would silently skip each other's work.
/// </remarks>
public sealed class InboxMessage
{
    public Guid MessageId { get; }
    public DateTimeOffset ProcessedOn { get; }

    public static InboxMessage Create(Guid messageId, DateTimeOffset processedOn)
    {
        Ensure.NotEmpty(messageId);
        Ensure.NotEmpty(processedOn);
        InboxMessage instance = new(messageId: messageId, processedOn: processedOn);
        return instance;
    }

    private InboxMessage(Guid messageId, DateTimeOffset processedOn)
    {
        MessageId = messageId;
        ProcessedOn = processedOn;
    }

    private InboxMessage()
    {
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/LotroKoniecDev.AuthSystem.API.Tests.Unit --filter "FullyQualifiedName~InboxMessageTests"`
Expected: 3 PASS. (The unit test project reaches `Persistence` transitively through its `API` project reference — no csproj edit needed.)

- [ ] **Step 5: Commit**

```bash
git add src/AuthSystem/LotroKoniecDev.AuthSystem.Persistence/Inbox/InboxMessage.cs \
        tests/LotroKoniecDev.AuthSystem.API.Tests.Unit/Inbox/InboxMessageTests.cs
git commit -m "Add the inbox message that records a processed delivery id"
```

(Include the Global Constraints trailer in this and every commit message.)

---

### Task 2: EF configuration + `DbSet` + migration

**Files:**
- Create: `src/AuthSystem/LotroKoniecDev.AuthSystem.Persistence/Configurations/InboxMessageConfiguration.cs`
- Modify: `src/AuthSystem/LotroKoniecDev.AuthSystem.Persistence/DbContexts/AuthDbContext.cs`
- Generated: `src/AuthSystem/LotroKoniecDev.AuthSystem.Persistence/Migrations/<timestamp>_AddInboxMessages.cs` (+ `.Designer.cs`, + snapshot update)

**Interfaces:**
- Consumes: `InboxMessage` from Task 1.
- Produces: `AuthDbContext.InboxMessages` (`DbSet<InboxMessage>`) — Task 3 queries and inserts through it. Table `authsystem."InboxMessages"`.

- [ ] **Step 1: Write the configuration**

Create `src/AuthSystem/LotroKoniecDev.AuthSystem.Persistence/Configurations/InboxMessageConfiguration.cs`:

```csharp
using LotroKoniecDev.AuthSystem.Persistence.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LotroKoniecDev.AuthSystem.Persistence.Configurations;

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("InboxMessages");

        builder.HasKey(message => message.MessageId);

        // Both properties are get-only, and EF Core's convention only discovers properties that
        // have BOTH a getter and a setter — each needs an explicit Property() call to exist in
        // the model at all (same gotcha as OutboxMessageConfiguration).
        builder.Property(message => message.MessageId)
            .ValueGeneratedNever();

        builder.Property(message => message.ProcessedOn);
    }
}
```

(No index beyond the PK: the only query is a PK lookup. The configuration is picked up automatically — `AuthDbContext.OnModelCreating` calls `ApplyConfigurationsFromAssembly`.)

- [ ] **Step 2: Add the DbSet**

In `src/AuthSystem/LotroKoniecDev.AuthSystem.Persistence/DbContexts/AuthDbContext.cs`, add the using and the property (directly under the `OutboxMessages` DbSet):

```csharp
using LotroKoniecDev.AuthSystem.Persistence.Inbox;
```

```csharp
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
```

- [ ] **Step 3: Generate the migration**

From the repo root (`dotnet-ef` is a pinned local tool; Auth resolves through its API project — only it carries EF Core Design — while the migration files land in Persistence; the design-time factory accepts `--connection`, so no live database is needed):

```bash
dotnet tool restore
dotnet ef migrations add AddInboxMessages \
  --project src/AuthSystem/LotroKoniecDev.AuthSystem.Persistence \
  --startup-project src/AuthSystem/LotroKoniecDev.AuthSystem.API \
  --context AuthDbContext \
  -- --connection "Host=localhost;Database=lotro_auth;Username=postgres;Password=changeme"
```

- [ ] **Step 4: Verify the migration is purely additive**

Open the generated `<timestamp>_AddInboxMessages.cs` and check `Up()` contains EXACTLY one operation: `CreateTable` for `InboxMessages` in schema `authsystem` with columns `MessageId` (uuid, PK) and `ProcessedOn` (timestamp with time zone), and nothing else. Any extra operation (a `DropColumn`, an `AlterColumn`) means the model drifted — most likely a get-only property missing its explicit `Property()` call — fix the configuration and regenerate (`dotnet ef migrations remove` with the same arguments, then re-add).

- [ ] **Step 5: Build and run the auth unit suite**

Run: `dotnet build LotroKoniecDev.slnx` then `dotnet test tests/LotroKoniecDev.AuthSystem.API.Tests.Unit`
Expected: zero warnings, all PASS. (The integration suite applies migrations automatically per-container; local compose users rebuild via `docker compose up --build migrator` — nothing to do in this plan.)

- [ ] **Step 6: Commit**

```bash
git add src/AuthSystem/LotroKoniecDev.AuthSystem.Persistence
git commit -m "Add the inbox table to the auth database"
```

---

### Task 3: `EmailConfirmationDeliveryProcessor` + EventIds + DI + integration tests

**Files:**
- Create: `src/AuthSystem/LotroKoniecDev.AuthSystem.API/Services/Emails/EmailConfirmationDeliveryProcessor.cs`
- Modify: `src/AuthSystem/LotroKoniecDev.AuthSystem.API/EventIds.cs` (new ids)
- Modify: `src/AuthSystem/LotroKoniecDev.AuthSystem.API/ApiDependencyInjection.cs` (one registration)
- Test: `tests/LotroKoniecDev.AuthSystem.API.Tests.Integration/Tests/Auth/InboxDeduplicationTests.cs`

**Interfaces:**
- Consumes: `AuthDbContext.InboxMessages` + `InboxMessage.Create` (Tasks 1–2); existing `EmailConfirmationRequestProcessor.ProcessAsync(EmailConfirmationRequested, CancellationToken)` returning `Result`; `TimeProvider` (registered singleton); `EmailConfirmationRequested(Guid IdentityUserId)` record from `LotroKoniecDev.AuthSystem.API.Outbox`.
- Produces: `internal sealed partial class EmailConfirmationDeliveryProcessor` (scoped) with `Task<Result> ProcessOnceAsync(EmailConfirmationRequested message, Guid messageId, CancellationToken cancellationToken)`. Task 4 resolves it in the consumer and the factory bridge.

- [ ] **Step 1: Add the EventIds**

In `src/AuthSystem/LotroKoniecDev.AuthSystem.API/EventIds.cs`, after the `// Email Confirmation Consumer (2330–2339)` block (which is full), add:

```csharp
    // Inbox deduplication (2340–2349)
    public const int EmailConsumerDuplicateSkipped = 2340;
    public const int EmailConsumerInboxRaceLost = 2341;
    public const int EmailConsumerMessageIdUnusable = 2342;
```

(`EmailConsumerMessageIdUnusable` is used by Task 4's consumer change — adding it now keeps the block in one commit. An unused const raises no warning.)

- [ ] **Step 2: Write the failing integration tests**

Create `tests/LotroKoniecDev.AuthSystem.API.Tests.Integration/Tests/Auth/InboxDeduplicationTests.cs`. It mirrors `OutboxRelayTests` (same base class, same wait-on-state idiom — never wait on time). The API assembly's internals are already visible to this test project (the factory resolves the internal `EmailConfirmationRequestProcessor` today):

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Inbox;
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
```

Note on the deliberate test gap: the primary-key-violation branch (`EmailConsumerInboxRaceLost`, two racing inserts of the same id) is NOT integration-tested — hitting it black-box requires interleaving a competing insert between the component's check and its save, which no seam allows. The branch is three lines of defensive code reviewed in Task 5; do not add a seam just to reach it.

- [ ] **Step 3: Run the new tests to verify they fail**

Run: `dotnet test tests/LotroKoniecDev.AuthSystem.API.Tests.Integration --filter "FullyQualifiedName~InboxDeduplicationTests"`
Expected: build FAILURE — `EmailConfirmationDeliveryProcessor` does not exist yet.

- [ ] **Step 4: Write the component**

Create `src/AuthSystem/LotroKoniecDev.AuthSystem.API/Services/Emails/EmailConfirmationDeliveryProcessor.cs` (`PostgresException`/`PostgresErrorCodes` come from Npgsql, available transitively through the Persistence reference):

```csharp
using Microsoft.EntityFrameworkCore;
using Npgsql;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Inbox;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// The delivery-level wrapper around <see cref="EmailConfirmationRequestProcessor"/>: consults the
/// inbox before doing any work and records the message id after success (ADR-0037). Both real
/// delivery paths — <see cref="BackgroundServices.EmailConfirmationConsumer"/> and the integration
/// suite's broker-less bridge — resolve this one component, so the dedup logic cannot drift
/// between them.
/// </summary>
/// <remarks>
/// Returns the same ack-decision contract as the processor: success means "ack, drop it from the
/// queue" (processed now, or already processed before), failure means "worth redelivering". The
/// inbox row lands AFTER the send on purpose — recording first would trade duplicate-e-mail risk
/// for lost-e-mail risk (ADR-0037 Decision 2). Database faults deliberately escape as exceptions:
/// the consumer's existing transient path rejects the delivery and the broker's delivery limit
/// bounds the loop (ADR-0037 Decision 4).
/// </remarks>
internal sealed partial class EmailConfirmationDeliveryProcessor
{
    private readonly AuthDbContext _db;
    private readonly EmailConfirmationRequestProcessor _processor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EmailConfirmationDeliveryProcessor> _logger;

    public EmailConfirmationDeliveryProcessor(
        AuthDbContext db,
        EmailConfirmationRequestProcessor processor,
        TimeProvider timeProvider,
        ILogger<EmailConfirmationDeliveryProcessor> logger)
    {
        _db = db;
        _processor = processor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<Result> ProcessOnceAsync(
        EmailConfirmationRequested message,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        bool alreadyProcessed = await _db.InboxMessages
            .AsNoTracking()
            .AnyAsync(inboxMessage => inboxMessage.MessageId == messageId, cancellationToken);
        if (alreadyProcessed)
        {
            LogDuplicateSkipped(_logger, messageId);
            return Result.Success();
        }

        Result ackDecision = await _processor.ProcessAsync(message, cancellationToken);
        if (ackDecision.IsFailure)
        {
            return ackDecision;
        }

        _db.InboxMessages.Add(InboxMessage.Create(messageId, _timeProvider.GetUtcNow()));

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsPrimaryKeyViolation(ex))
        {
            // A concurrent duplicate won the insert race, which means the work is done — same
            // ack decision as a pre-check hit.
            LogInboxRaceLost(_logger, messageId);
        }

        return Result.Success();
    }

    private static bool IsPrimaryKeyViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    }

    [LoggerMessage(
        EventId = EventIds.EmailConsumerDuplicateSkipped,
        Level = LogLevel.Information,
        Message = "Skipping message {MessageId}: the inbox already recorded it as processed")]
    private static partial void LogDuplicateSkipped(ILogger logger, Guid messageId);

    [LoggerMessage(
        EventId = EventIds.EmailConsumerInboxRaceLost,
        Level = LogLevel.Information,
        Message = "Recording message {MessageId} lost an insert race to a concurrent duplicate; acknowledging as processed")]
    private static partial void LogInboxRaceLost(ILogger logger, Guid messageId);
}
```

- [ ] **Step 5: Register it in DI**

In `src/AuthSystem/LotroKoniecDev.AuthSystem.API/ApiDependencyInjection.cs`, directly under the existing `services.AddScoped<EmailConfirmationRequestProcessor>();` line, add:

```csharp
            services.AddScoped<EmailConfirmationDeliveryProcessor>();
```

- [ ] **Step 6: Run the new tests to verify they pass**

Run: `dotnet test tests/LotroKoniecDev.AuthSystem.API.Tests.Integration --filter "FullyQualifiedName~InboxDeduplicationTests"`
Expected: 3 PASS (Docker must be running).

- [ ] **Step 7: Commit**

```bash
git add src/AuthSystem/LotroKoniecDev.AuthSystem.API \
        tests/LotroKoniecDev.AuthSystem.API.Tests.Integration/Tests/Auth/InboxDeduplicationTests.cs
git commit -m "Skip deliveries the inbox already recorded"
```

---

### Task 4: Wire the consumer and the suite's bridge through the shared component

**Files:**
- Modify: `src/AuthSystem/LotroKoniecDev.AuthSystem.API/BackgroundServices/EmailConfirmationConsumer.cs`
- Modify: `tests/LotroKoniecDev.AuthSystem.API.Tests.Integration/AuthSystemApiFactory.cs` (bridge method `DeliverLikeTheConsumerWouldAsync`)
- Test: `tests/LotroKoniecDev.AuthSystem.API.Tests.Unit/Messaging/EmailConfirmationConsumerTests.cs` (extend)

**Interfaces:**
- Consumes: `EmailConfirmationDeliveryProcessor.ProcessOnceAsync(EmailConfirmationRequested, Guid, CancellationToken)` from Task 3; `EventIds.EmailConsumerMessageIdUnusable` from Task 3 Step 1.
- Produces: `internal static bool EmailConfirmationConsumer.TryReadMessageId(IReadOnlyBasicProperties properties, out Guid messageId)` — unit-tested here, used nowhere else.

- [ ] **Step 1: Write the failing unit tests**

In `tests/LotroKoniecDev.AuthSystem.API.Tests.Unit/Messaging/EmailConfirmationConsumerTests.cs`, add `using RabbitMQ.Client;` to the usings and append inside the class:

```csharp
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void TryReadMessageId_WithUnusableValue_ReturnsFalse(string? rawMessageId)
    {
        // Arrange — BasicProperties implements IReadOnlyBasicProperties
        BasicProperties properties = new() { MessageId = rawMessageId };

        // Act
        bool usable = EmailConfirmationConsumer.TryReadMessageId(properties, out Guid _);

        // Assert
        usable.ShouldBeFalse();
    }

    [Fact]
    public void TryReadMessageId_WithGuidValue_ReturnsTheParsedId()
    {
        // Arrange
        Guid messageId = Guid.CreateVersion7();
        BasicProperties properties = new() { MessageId = messageId.ToString() };

        // Act
        bool usable = EmailConfirmationConsumer.TryReadMessageId(properties, out Guid parsed);

        // Assert
        usable.ShouldBeTrue();
        parsed.ShouldBe(messageId);
    }
```

Also update the class's `<summary>` first sentence to cover the new concern, e.g. append: `Also pins the poison decision for unusable message ids (ADR-0037): a delivery the inbox cannot deduplicate must be rejected, not processed blind.`

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/LotroKoniecDev.AuthSystem.API.Tests.Unit --filter "FullyQualifiedName~EmailConfirmationConsumerTests"`
Expected: build FAILURE — `TryReadMessageId` does not exist.

- [ ] **Step 3: Change the consumer**

In `src/AuthSystem/LotroKoniecDev.AuthSystem.API/BackgroundServices/EmailConfirmationConsumer.cs`:

**(a)** In `OnDeliveredAsync`, insert the message-id gate at the very top of the `try` block, BEFORE the existing `TryDeserialize` call:

```csharp
            if (!TryReadMessageId(delivery.BasicProperties, out Guid messageId))
            {
                // Poison: without a usable message id the delivery cannot be deduplicated, and
                // processing it blind would reopen the unbounded-duplicate hole the inbox closes
                // (ADR-0037) — so it parks in the dead-letter queue for a human instead.
                LogMessageIdUnusable(_logger, delivery.BasicProperties.MessageId);
                await channel.BasicRejectAsync(
                    delivery.DeliveryTag,
                    requeue: false,
                    cancellationToken: stoppingToken);
                return;
            }
```

**(b)** Replace the processor resolution block

```csharp
            Result ackDecision;
            await using (AsyncServiceScope scope = _scopeFactory.CreateAsyncScope())
            {
                EmailConfirmationRequestProcessor processor =
                    scope.ServiceProvider.GetRequiredService<EmailConfirmationRequestProcessor>();
                ackDecision = await processor.ProcessAsync(message, stoppingToken);
            }
```

with:

```csharp
            Result ackDecision;
            await using (AsyncServiceScope scope = _scopeFactory.CreateAsyncScope())
            {
                EmailConfirmationDeliveryProcessor deliveryProcessor =
                    scope.ServiceProvider.GetRequiredService<EmailConfirmationDeliveryProcessor>();
                ackDecision = await deliveryProcessor.ProcessOnceAsync(message, messageId, stoppingToken);
            }
```

**(c)** Add the helper (place it directly above `TryDeserialize`):

```csharp
    /// <summary>
    /// Reads the broker message id the inbox deduplicates on (ADR-0037). Internal so the unit
    /// suite can pin the poison decision: absent, non-Guid and empty-Guid ids must all fail.
    /// </summary>
    internal static bool TryReadMessageId(IReadOnlyBasicProperties properties, out Guid messageId)
    {
        return Guid.TryParse(properties.MessageId, out messageId) && messageId != Guid.Empty;
    }
```

**(d)** Add the log method next to the other `LoggerMessage`s:

```csharp
    [LoggerMessage(
        EventId = EventIds.EmailConsumerMessageIdUnusable,
        Level = LogLevel.Error,
        Message = "Rejecting message with unusable message id {MessageId} into the dead-letter queue: the inbox cannot deduplicate a delivery without an id")]
    private static partial void LogMessageIdUnusable(ILogger logger, string? messageId);
```

**(e)** In the class-level `<remarks>`, after the sentence ending `— at-least-once, matching the outbox's own semantics (the processor stays idempotent, see its remarks).`, insert one sentence:

```
On top of that, deliveries are deduplicated on the broker message id through the inbox
(ADR-0037), so a redelivery of an already-processed message acks without a second e-mail.
```

- [ ] **Step 4: Rewire the suite's bridge**

In `tests/LotroKoniecDev.AuthSystem.API.Tests.Integration/AuthSystemApiFactory.cs`, in `DeliverLikeTheConsumerWouldAsync`, replace the scope block

```csharp
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        EmailConfirmationRequestProcessor processor =
            scope.ServiceProvider.GetRequiredService<EmailConfirmationRequestProcessor>();
        await processor.ProcessAsync(payload, CancellationToken.None);
```

with:

```csharp
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        EmailConfirmationDeliveryProcessor deliveryProcessor =
            scope.ServiceProvider.GetRequiredService<EmailConfirmationDeliveryProcessor>();
        await deliveryProcessor.ProcessOnceAsync(payload, message.MessageId, CancellationToken.None);
```

and extend the method's `<summary>` to say the bridge now includes the inbox dedup, e.g.: `... in a fresh scope per message, exactly like EmailConfirmationConsumer.OnDeliveredAsync — including the inbox deduplication of ADR-0037, which therefore runs under this suite's real PostgreSQL.`

- [ ] **Step 5: Run both auth suites**

Run: `dotnet test tests/LotroKoniecDev.AuthSystem.API.Tests.Unit && dotnet test tests/LotroKoniecDev.AuthSystem.API.Tests.Integration`
Expected: all PASS — the new unit theories, the Task 3 tests still green through the rewired bridge, and every pre-existing registration/relay test unaffected (each relay publish carries a fresh outbox-row id, so the dedup never suppresses a legitimate first delivery).

- [ ] **Step 6: Commit**

```bash
git add src/AuthSystem/LotroKoniecDev.AuthSystem.API/BackgroundServices/EmailConfirmationConsumer.cs \
        tests/LotroKoniecDev.AuthSystem.API.Tests.Unit/Messaging/EmailConfirmationConsumerTests.cs \
        tests/LotroKoniecDev.AuthSystem.API.Tests.Integration/AuthSystemApiFactory.cs
git commit -m "Deduplicate broker deliveries through the inbox"
```

---

### Task 5: Align ADR-0037's implementation notes + full verification

**Files:**
- Modify: `docs/adr/0037-consumer-side-inbox-deduplication-on-the-message-id.md` (Implementation Notes only — the Decisions stand)

**Interfaces:** none — documentation + verification.

- [ ] **Step 1: Update the ADR's Implementation Notes**

The ADR was written before the test-architecture constraint surfaced (the broker-less integration suite bridges deliveries around the consumer, so consumer-inlined dedup would be untestable there without a drifting copy). Replace the second bullet of `## Implementation Notes`:

```
- `EmailConfirmationConsumer.OnDeliveredAsync` — the only consumer change: id parse (poison on
  absence), inbox lookup, post-success insert; `AuthDbContext` resolved from the existing
  per-delivery scope like the relay does — no new abstraction. A primary-key violation on the
  insert is treated as "already processed" (ack): a concurrent duplicate lost the race, which
  means the work is done.
```

with:

```
- `EmailConfirmationDeliveryProcessor` (scoped, `Services/Emails/`) — the inbox lookup, the
  delegation to `EmailConfirmationRequestProcessor` and the post-success insert live in this one
  component because two delivery paths must run them identically: the broker consumer and the
  integration suite's broker-less bridge (`AuthSystemApiFactory`), which would otherwise carry a
  drifting copy of the dedup logic. `AuthDbContext` comes from the existing per-delivery scope
  like the relay's does. A primary-key violation on the insert is treated as "already processed"
  (ack): a concurrent duplicate lost the race, which means the work is done.
- `EmailConfirmationConsumer.OnDeliveredAsync` — id parse (poison on an unusable id, pinned by
  unit tests) and the swap to the delivery processor; every failure path stays as ADR-0036 left
  it.
```

and replace the tests bullet:

```
- Tests, matching the branch's discipline: unit (`InboxMessage` factory guards) + integration
  against the real broker (duplicate publish of one message id → exactly one e-mail via the
  spy sender, one inbox row, empty queue; processor failure → no inbox row, redelivery really
  retries; missing message id → parks in the DLQ, zero e-mails).
```

with:

```
- Tests, matching the branch's discipline: unit (`InboxMessage` factory guards; message-id
  parsing incl. absent/non-Guid/empty) + integration against real PostgreSQL through the shared
  delivery component (duplicate delivery of one message id → exactly one e-mail via the spy
  sender and one inbox row; failed processing → no row, the healed redelivery really retries).
  The broker's side of the poison path — reject parks in the DLQ — is already pinned by
  `DeadLetterTopologyTests` against a real broker.
```

- [ ] **Step 2: Full build + full runnable test sweep**

Run: `dotnet build LotroKoniecDev.slnx` (zero warnings) and `dotnet test tests/LotroKoniecDev.AuthSystem.API.Tests.Unit && dotnet test tests/LotroKoniecDev.AuthSystem.API.Tests.Integration`
Expected: zero warnings, all PASS.

- [ ] **Step 3: Commit**

```bash
git add docs/adr/0037-consumer-side-inbox-deduplication-on-the-message-id.md
git commit -m "Align ADR-0037 implementation notes with the delivery seam"
```

---

## Plan Self-Review (done at authoring time)

- **Spec coverage:** ADR Decisions 1–6 map to Tasks 1–4 (table → T1/T2; check/process/record → T3; poison on unusable id → T4; DB faults escape to the existing transient path → T3 remarks + unchanged consumer catch-all; no discriminator / no retention → entity remarks, nothing to build). The failure-ordering analysis required no code — the ordering falls out of T3's check-before-process.
- **Placeholders:** none; every code step carries the full code.
- **Type consistency:** `InboxMessage.Create(Guid, DateTimeOffset)` (T1) = usage in T3; `ProcessOnceAsync(EmailConfirmationRequested, Guid, CancellationToken)` (T3) = usage in T4 consumer and bridge; `EventIds.EmailConsumerMessageIdUnusable` added in T3 Step 1, consumed in T4 Step 3d.
