using LotroKoniecDev.Architecture.Tests.Unit.Shared;
using NetArchTest.Rules;

namespace LotroKoniecDev.Architecture.Tests.Unit.Tests;

/// <summary>
/// ADR-0001: no mediator, repo-wide. Every lifted KittySaver slice is de-mediatorized on entry and the
/// <c>Mediator</c> / <c>MediatR</c> packages are forbidden — one use case is one record plus one handler
/// implementing the in-house <c>ICommandHandler</c> / <c>IQueryHandler</c>.
/// </summary>
/// <remarks>
/// The rule is asserted over IL, so it catches USE of a mediator type. A package restored but never
/// touched leaves no trace in IL and slips through — harmless on its own, and the moment anyone reaches
/// for it this test goes red.
/// </remarks>
public sealed class NoMediatorTests
{
    [Fact]
    public void ProductionAssemblies_Dependencies_NeverIncludeAMediator()
    {
        TestResult result = Types.InAssemblies(ProductionAssemblies.All)
            .Should()
            .NotHaveDependencyOnAny(Namespaces.MediatR, Namespaces.Mediator)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"ADR-0001 forbids Mediator/MediatR — inject the closed handler interface instead:{result.Describe()}");
    }
}
