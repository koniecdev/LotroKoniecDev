using LotroKoniecDev.Architecture.Tests.Unit.Shared;
using NetArchTest.Rules;

namespace LotroKoniecDev.Architecture.Tests.Unit.Tests;

/// <summary>
/// Persistence points one way: the domain and the outward-facing contracts know nothing about EF Core or
/// PostgreSQL, and the POCO read models stay free of the EF configurations that map them.
/// </summary>
/// <remarks>
/// <c>AuthSystem.Domain</c> is deliberately out of scope: it is a wholesale lift of an ASP.NET Core
/// Identity model (<c>ApplicationUser : IdentityUser&lt;Guid&gt;</c>), so the Identity EF Core package IS
/// its domain vocabulary. Removing it would mean rewriting the auth server, not fixing a leak.
/// </remarks>
public sealed class PersistenceDirectionTests
{
    [Fact]
    public void PatcherDomainAssembly_Dependencies_IncludeNoPersistenceTechnology()
    {
        TestResult result = Types.InAssembly(ProductionAssemblies.PatcherDomain)
            .Should()
            .NotHaveDependencyOnAny(Namespaces.EntityFrameworkCore, Namespaces.Npgsql)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"The patcher domain models a binary file format — it has no database at all:{result.Describe()}");
    }

    [Fact]
    public void TranslationSystemDomainAssembly_Dependencies_IncludeNoPersistenceTechnology()
    {
        TestResult result = Types.InAssembly(ProductionAssemblies.TranslationSystemDomain)
            .Should()
            .NotHaveDependencyOnAny(
                Namespaces.EntityFrameworkCore,
                Namespaces.Npgsql,
                Namespaces.TranslationSystemPersistence)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Aggregates stay persistence-ignorant — the mapping lives in Persistence, behind repositories:{result.Describe()}");
    }

    [Fact]
    public void ReadModelsAssembly_Dependencies_IncludeNoPersistenceTechnology()
    {
        TestResult result = Types.InAssembly(ProductionAssemblies.TranslationSystemReadModels)
            .Should()
            .NotHaveDependencyOnAny(
                Namespaces.EntityFrameworkCore,
                Namespaces.Npgsql,
                Namespaces.TranslationSystemDomain,
                Namespaces.TranslationSystemPersistence)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Read models are POCOs — their EF configurations live in the separate ReadModels.EntityFramework project:{result.Describe()}");
    }

    [Fact]
    public void ContractsAssembly_Dependencies_IncludeNeitherPersistenceTechnologyNorTheDomain()
    {
        TestResult result = Types.InAssembly(ProductionAssemblies.TranslationSystemContracts)
            .Should()
            .NotHaveDependencyOnAny(
                Namespaces.EntityFrameworkCore,
                Namespaces.Npgsql,
                Namespaces.TranslationSystemDomain,
                Namespaces.TranslationSystemReadModels,
                Namespaces.TranslationSystemPersistence)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Contracts is the wire format the Frontend references — anything leaking in leaks all the way out:{result.Describe()}");
    }
}
