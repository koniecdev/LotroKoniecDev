using System.Reflection;
using FluentValidation;
using LotroKoniecDev.Architecture.Tests.Unit.Shared;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using PatcherMessaging = LotroKoniecDev.Application.Abstractions.Messaging;
using TranslationSystemMessaging = LotroKoniecDev.SharedKernel.Messaging;

namespace LotroKoniecDev.Architecture.Tests.Unit.Tests;

/// <summary>
/// The CQRS read/write split (ADR-0002 amendment): queries read POCO read models through
/// <c>IApplicationReadDbContext</c>; commands load and mutate aggregates through repositories plus
/// <c>IUnitOfWork</c>. Handlers use explicit constructor DI, so a constructor's parameters ARE the
/// handler's dependency list.
/// </summary>
/// <remarks>
/// The mirror-image rule — "a command handler may not see the read context" — is deliberately NOT
/// asserted: <c>UpsertTranslation</c> re-reads the committed row through the read model so the response
/// carries the joined submitter/approver display names (ADR-0004). The split that matters is
/// one-directional: the write model never serves a query.
/// </remarks>
public sealed class CqrsSeparationTests
{
    private static readonly Type[] QueryHandlerInterfaces =
    [
        typeof(TranslationSystemMessaging.IQueryHandler<,>),
        typeof(PatcherMessaging.IQueryHandler<,>),
    ];

    private static readonly Type[] CommandHandlerInterfaces =
    [
        typeof(TranslationSystemMessaging.ICommandHandler<,>),
        typeof(PatcherMessaging.ICommandHandler<,>),
    ];

    [Fact]
    public void QueryHandlers_ConstructorDependencies_NeverReachTheWriteSide()
    {
        IReadOnlyList<Type> queryHandlers = ProductionTypes.ImplementingAny(QueryHandlerInterfaces);
        queryHandlers.ShouldNotBeEmpty("no query handler was discovered — the rule would pass vacuously");

        List<string> violations = queryHandlers
            .SelectMany(handler => DependenciesOf(handler)
                .Where(IsWriteSide)
                .Select(dependency => $"{handler.FullName} <- {dependency.Name}"))
            .ToList();

        violations.ShouldBeEmpty(
            $"A query reads the read models — repositories, the unit of work and the write DbContext are the command side:{ViolationReport.Format(violations)}");
    }

    [Fact]
    public void CommandHandlers_ConstructorDependencies_AlwaysIncludeTheValidatorOfTheirCommand()
    {
        IReadOnlyList<Type> commandHandlers = ProductionTypes.ImplementingAny(CommandHandlerInterfaces);
        commandHandlers.ShouldNotBeEmpty("no command handler was discovered — the rule would pass vacuously");

        List<string> violations = commandHandlers
            .Where(handler => !InjectsItsOwnValidator(handler))
            .Select(handler => handler.FullName!)
            .ToList();

        violations.ShouldBeEmpty(
            $"FluentValidation is for commands: the handler injects IValidator<TCommand> and maps failures to a Result, never throws:{ViolationReport.Format(violations)}");
    }

    private static bool InjectsItsOwnValidator(Type handler)
    {
        IReadOnlyList<Type> dependencies = DependenciesOf(handler);

        return ProductionTypes.ClosedInterfacesOf(handler, CommandHandlerInterfaces)
            .Select(handlerInterface => typeof(IValidator<>).MakeGenericType(handlerInterface.GetGenericArguments()[0]))
            .All(dependencies.Contains);
    }

    private static IReadOnlyList<Type> DependenciesOf(Type handler) =>
        handler.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToList();

    private static bool IsWriteSide(Type dependency) =>
        dependency == typeof(IUnitOfWork)
        || ProductionTypes.Implements(dependency, typeof(IRepository<,>))
        || dependency.Namespace?.StartsWith(Namespaces.TranslationSystemWriteDbContexts, StringComparison.Ordinal) == true;
}
