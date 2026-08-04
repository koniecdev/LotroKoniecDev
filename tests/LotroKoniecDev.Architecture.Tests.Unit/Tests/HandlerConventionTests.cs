using FluentValidation;
using LotroKoniecDev.Architecture.Tests.Unit.Shared;
using PatcherMessaging = LotroKoniecDev.Application.Abstractions.Messaging;
using TranslationSystemMessaging = LotroKoniecDev.SharedKernel.Messaging;

namespace LotroKoniecDev.Architecture.Tests.Unit.Tests;

/// <summary>
/// Shape rules for the no-mediator slice (ADR-0001): a handler is an implementation detail of its slice,
/// and FluentValidation is a command-side tool.
/// </summary>
public sealed class HandlerConventionTests
{
    private static readonly Type[] HandlerInterfaces =
    [
        typeof(TranslationSystemMessaging.ICommandHandler<,>),
        typeof(TranslationSystemMessaging.IQueryHandler<,>),
        typeof(PatcherMessaging.ICommandHandler<,>),
        typeof(PatcherMessaging.IQueryHandler<,>),
    ];

    private static readonly Type[] QueryInterfaces =
    [
        typeof(TranslationSystemMessaging.IQuery<>),
        typeof(PatcherMessaging.IQuery<>),
    ];

    [Fact]
    public void CommandAndQueryHandlers_Declaration_IsSealedAndNotPubliclyVisible()
    {
        IReadOnlyList<Type> handlers = ProductionTypes.ImplementingAny(HandlerInterfaces);
        handlers.ShouldNotBeEmpty("no handler was discovered — the rule would pass vacuously");

        List<string> violations = handlers
            .Where(handler => !handler.IsSealed || handler.IsVisible)
            .Select(handler => handler.FullName!)
            .ToList();

        violations.ShouldBeEmpty(
            $"Consumers inject the CLOSED handler interface, never the class — so a handler is internal and sealed:{ViolationReport.Format(violations)}");
    }

    [Fact]
    public void FluentValidationValidators_ValidatedType_IsNeverAQuery()
    {
        IReadOnlyList<Type> validators = ProductionTypes.ImplementingAny(typeof(IValidator<>));
        validators.ShouldNotBeEmpty("no validator was discovered — the rule would pass vacuously");

        List<string> violations = validators
            .SelectMany(validator => ProductionTypes.ClosedInterfacesOf(validator, typeof(IValidator<>))
                .Select(closedValidator => closedValidator.GetGenericArguments()[0])
                .Where(validatedType => ProductionTypes.ClosedInterfacesOf(validatedType, QueryInterfaces).Count > 0)
                .Select(validatedType => $"{validator.FullName} -> {validatedType.Name}"))
            .ToList();

        violations.ShouldBeEmpty(
            $"Validators are for commands only — a query validates inline in its own handler:{ViolationReport.Format(violations)}");
    }
}
