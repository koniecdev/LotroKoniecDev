using LotroKoniecDev.Architecture.Tests.Unit.Shared;
using NetArchTest.Rules;

namespace LotroKoniecDev.Architecture.Tests.Unit.Tests;

/// <summary>
/// ADR-0001: no <c>Mediator</c> or <c>MediatR</c> anywhere. A use case is one record plus one handler on
/// our own <c>ICommandHandler</c> or <c>IQueryHandler</c>.
/// </summary>
/// <remarks>
/// The rule reads the IL, so a package that is restored but never used passes. The first use of it
/// turns this test red.
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
