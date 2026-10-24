using FluentValidation;
using FluentValidation.Results;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translators;

/// <summary>
/// No HTTP endpoint: only the <c>AccountErased</c> consumer sends it (ADR-0065). An account with no
/// profile counts as done, and erasing twice changes nothing, so the consumer needs no inbox.
/// </summary>
internal static class EraseTranslatorProfile
{
    internal sealed record Command(IdentityId IdentityId) : ICommand<Result>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(command => command.IdentityId)
                .NotEqual(IdentityId.Empty)
                .WithMessage("The identity id is required.");
        }
    }

    internal sealed class Handler : ICommandHandler<Command, Result>
    {
        private readonly IValidator<Command> _validator;
        private readonly ITranslatorRepository _translatorRepository;
        private readonly IUnitOfWork _unitOfWork;

        public Handler(
            IValidator<Command> validator,
            ITranslatorRepository translatorRepository,
            IUnitOfWork unitOfWork)
        {
            _validator = validator;
            _translatorRepository = translatorRepository;
            _unitOfWork = unitOfWork;
        }

        public async ValueTask<Result> Handle(Command command, CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                string message = string.Join("; ", validationResult.Errors.Select(failure => failure.ErrorMessage));
                return Result.Failure(new Error("Translators.Validation", message, TypeOfError.Validation));
            }

            Maybe<Translator> translatorMaybe =
                await _translatorRepository.GetByIdentityIdAsync(command.IdentityId, cancellationToken);
            if (translatorMaybe.HasNoValue)
            {
                return Result.Success();
            }

            translatorMaybe.Value.Erase();
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }
    }
}
