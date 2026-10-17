using LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Persistence;

/// <summary>
/// Architecture inventory over the write model (the AUDIT-EF-05 class, made permanent): a
/// strongly-typed-id column pointing at another aggregate that ships without an FK constraint
/// loses the database backstop against dangling pointers, and without an index every reference
/// guard scan on it is a sequential scan. Pure model introspection — no database.
/// </summary>
public sealed class WriteModelPointerInventoryTests
{
    /// <summary>
    /// Pointers that deliberately carry no FK constraint, keyed by "Entity.Property" with the
    /// reason. An entry here is a reviewed decision, not a shortcut.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ForeignKeyExemptions =
        new Dictionary<string, string>
        {
            [$"{nameof(Translator)}.{nameof(Translator.IdentityId)}"] =
                "Cross-bounded-context pointer: the AuthSystem identity lives in a separate database (ADR-0004), so no FK is possible; its unique index still covers lookups.",
        };

    [Fact]
    public void WriteModel_EveryStronglyTypedIdPointerColumn_HasForeignKeyOrDocumentedExemption()
    {
        IModel model = BuildWriteModel();

        List<string> offenders = CollectPointerProperties(model)
            .Where(pointer => !pointer.Property.GetContainingForeignKeys().Any())
            .Where(pointer => !ForeignKeyExemptions.ContainsKey(pointer.Name))
            .Select(pointer => pointer.Name)
            .ToList();

        offenders.ShouldBeEmpty(
            $"Pointer columns without an FK constraint: {string.Join(", ", offenders)}. " +
            "Map each with HasOne<Target>().WithMany().HasForeignKey(...) in its configuration " +
            "(DeleteBehavior.Restrict unless decided otherwise), or add a documented exemption here.");
    }

    [Fact]
    public void WriteModel_EveryStronglyTypedIdPointerColumn_IsFirstColumnOfAnIndex()
    {
        IModel model = BuildWriteModel();

        List<string> offenders = CollectPointerProperties(model)
            .Where(pointer => !pointer.Property.GetContainingIndexes()
                .Any(index => index.Properties[0] == pointer.Property))
            .Select(pointer => pointer.Name)
            .ToList();

        offenders.ShouldBeEmpty(
            $"Pointer columns no index starts with: {string.Join(", ", offenders)}. " +
            "Reference-guard scans on such a column are sequential scans; add HasIndex in the " +
            "configuration (the FK convention normally provides one — check why it did not).");
    }

    [Fact]
    public void WriteModel_EveryForeignKeyExemption_StillMatchesAnExistingPointerColumn()
    {
        IModel model = BuildWriteModel();

        HashSet<string> pointerNames = CollectPointerProperties(model)
            .Select(pointer => pointer.Name)
            .ToHashSet();

        List<string> staleExemptions = ForeignKeyExemptions.Keys
            .Where(exemption => !pointerNames.Contains(exemption))
            .ToList();

        staleExemptions.ShouldBeEmpty(
            $"Exemptions no longer matching any pointer column: {string.Join(", ", staleExemptions)}. " +
            "Remove them so the whitelist stays an inventory of live decisions.");
    }

    private static List<(string Name, IProperty Property)> CollectPointerProperties(IModel model)
    {
        return model.GetEntityTypes()
            .SelectMany(entityType => entityType.GetProperties()
                .Where(property => !property.IsPrimaryKey())
                .Where(property => IsStronglyTypedId(property.ClrType))
                .Select(property => (Name: $"{entityType.ClrType.Name}.{property.Name}", Property: property)))
            .ToList();
    }

    private static bool IsStronglyTypedId(Type clrType)
    {
        Type unwrapped = Nullable.GetUnderlyingType(clrType) ?? clrType;

        return unwrapped.GetInterfaces().Any(candidate =>
            candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IStronglyTypedId<>));
    }

    private static IModel BuildWriteModel()
    {
        DbContextOptions<ApplicationWriteDbContext> options =
            new DbContextOptionsBuilder<ApplicationWriteDbContext>()
                .UseNpgsql()
                .Options;

        using ApplicationWriteDbContext context = new(options);

        return context.Model;
    }
}
