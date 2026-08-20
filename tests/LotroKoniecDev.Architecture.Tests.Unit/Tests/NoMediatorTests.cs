using LotroKoniecDev.Architecture.Tests.Unit.Shared;
using NetArchTest.Rules;

namespace LotroKoniecDev.Architecture.Tests.Unit.Tests;

/// <summary>
/// ADR-0001: no mediator anywhere in the repo. Every slice lifted from KittySaver has its mediator
/// removed on the way in, and the <c>Mediator</c> and <c>MediatR</c> packages are forbidden. One use
/// case is one record plus one handler implementing our own <c>ICommandHandler</c> or
/// <c>IQueryHandler</c>.
/// </summary>
/// <remarks>
/// The rule is checked over the IL, so it catches any use of a mediator type. A package that is restored
/// but never used leaves nothing in the IL and passes, which is harmless on its own: the moment anyone
/// uses it, this test turns red.
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
