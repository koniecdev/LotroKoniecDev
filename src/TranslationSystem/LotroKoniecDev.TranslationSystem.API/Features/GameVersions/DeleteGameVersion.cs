using FluentValidation;
using FluentValidation.Results;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Features.GameVersions;

/// <summary>
/// Deletes a manually-registered game version that was added by mistake (#209). Only a
/// <see cref="Primitives.Aggregates.GameVersionAggregate.Enums.GameVersionStatus.Processed"/> version is
/// kept — it is the one an import was applied against, and removing it would orphan the rows that point
/// at it (422). An unprocessed or superseded version with no translation referencing it can be removed,
/// which is what frees a version number burned by a wrong registration (#624). Requires the admin
/// policy; an unknown id is a 404.
/// </summary>
internal sealed class DeleteGameVersion : IEndpoint
{
    internal sealed record Command(GameVersionId Id) : ICommand<Result>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(command => command.Id)
                .NotEqual(GameVersionId.Empty)
                .WithMessage("The game version id is required.");
        }
    }

    internal sealed class Handler : ICommandHandler<Command, Result>
    {
        private readonly IValidator<Command> _validator;
        private readonly IGameVersionRepository _gameVersionRepository;
        private readonly ITranslationRepository _translationRepository;
        private readonly IUnitOfWork _unitOfWork;

        public Handler(
            IValidator<Command> validator,
            IGameVersionRepository gameVersionRepository,
            ITranslationRepository translationRepository,
            IUnitOfWork unitOfWork)
        {
            _validator = validator;
            _gameVersionRepository = gameVersionRepository;
            _translationRepository = translationRepository;
            _unitOfWork = unitOfWork;
        }

        public async ValueTask<Result> Handle(Command command, CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                string message = string.Join("; ", validationResult.Errors.Select(failure => failure.ErrorMessage));
                return Result.Failure(new Error("GameVersions.Validation", message, TypeOfError.Validation));
            }

            Maybe<GameVersion> gameVersionMaybe = await _gameVersionRepository.GetByIdAsync(command.Id, cancellationToken);
            if (gameVersionMaybe.HasNoValue)
            {
                return Result.Failure(DomainErrors.GameVersionEntity.NotFound(command.Id));
            }

            GameVersion gameVersion = gameVersionMaybe.Value;

            Result deletableResult = gameVersion.EnsureCanBeDeleted();
            if (deletableResult.IsFailure)
            {
                return deletableResult;
            }

            // Cross-aggregate safety net: under the lifecycle neither an Unprocessed nor a Superseded
            // version has ever been imported against, so nothing should reference it — but a referenced
            // version must never be removed, or its translations would point at a missing version.
            if (await _translationRepository.AnyReferencesGameVersionAsync(command.Id, cancellationToken))
            {
                return Result.Failure(DomainErrors.GameVersionEntity.CannotDeleteReferencedVersion(command.Id));
            }

            _gameVersionRepository.Remove(gameVersion);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapDelete("/api/v1/game-versions/{id:guid}", async (
                Guid id,
                ICommandHandler<Command, Result> handler,
                CancellationToken cancellationToken) =>
            {
                Result result = await handler.Handle(new Command(GameVersionId.FromValue(id)), cancellationToken);

                return result.IsSuccess
                    ? Results.NoContent()
                    : Results.Problem(result.Error.ToProblemDetails());
            })
            .WithName(nameof(DeleteGameVersion))
            .WithTags("GameVersions")
            .RequireAuthorization(AuthorizationPolicies.RequireAdminRole)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }
}
