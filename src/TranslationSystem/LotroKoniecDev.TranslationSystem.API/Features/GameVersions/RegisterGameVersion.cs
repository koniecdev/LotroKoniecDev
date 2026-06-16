using FluentValidation;
using FluentValidation.Results;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;

namespace LotroKoniecDev.TranslationSystem.API.Features.GameVersions;

/// <summary>
/// Manually registers a game version (spec 0001): the degenerate fallback the admin uses when the
/// forum scrape breaks. Creates an Unprocessed version; a duplicate version string is a conflict
/// (the registration is idempotent in effect — the existing version stands).
/// </summary>
internal sealed class RegisterGameVersion : IEndpoint
{
    internal sealed record Command(string Version) : ICommand<Result<GameVersionResponse>>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(command => command.Version)
                .NotEmpty()
                .MaximumLength(LotroNotationVersion.VersionMaxLength);
        }
    }

    internal sealed class Handler : ICommandHandler<Command, Result<GameVersionResponse>>
    {
        private readonly IValidator<Command> _validator;
        private readonly IGameVersionRepository _gameVersionRepository;
        private readonly IUnitOfWork _unitOfWork;
        private readonly TimeProvider _timeProvider;

        public Handler(
            IValidator<Command> validator,
            IGameVersionRepository gameVersionRepository,
            IUnitOfWork unitOfWork,
            TimeProvider timeProvider)
        {
            _validator = validator;
            _gameVersionRepository = gameVersionRepository;
            _unitOfWork = unitOfWork;
            _timeProvider = timeProvider;
        }

        public async ValueTask<Result<GameVersionResponse>> Handle(Command command, CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                string message = string.Join("; ", validationResult.Errors.Select(failure => failure.ErrorMessage));
                return Result.Failure<GameVersionResponse>(new Error("GameVersions.Validation", message, TypeOfError.Validation));
            }

            Result<LotroNotationVersion> versionResult = LotroNotationVersion.Create(command.Version);
            if (versionResult.IsFailure)
            {
                return Result.Failure<GameVersionResponse>(versionResult.Error);
            }

            LotroNotationVersion version = versionResult.Value;

            if (await _gameVersionRepository.ExistsByVersionAsync(version, cancellationToken))
            {
                return Result.Failure<GameVersionResponse>(DomainErrors.GameVersionEntity.VersionAlreadyRegistered(version.Value));
            }

            Result<GameVersion> gameVersionResult = GameVersion.Create(version, _timeProvider.GetUtcNow());
            if (gameVersionResult.IsFailure)
            {
                return Result.Failure<GameVersionResponse>(gameVersionResult.Error);
            }

            GameVersion gameVersion = gameVersionResult.Value;
            _gameVersionRepository.Insert(gameVersion);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success(new GameVersionResponse(
                gameVersion.Id,
                gameVersion.LotroNotationVersion.Value,
                gameVersion.DetectedAt,
                gameVersion.Status));
        }
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("/api/v1/game-versions", async (
                RegisterGameVersionRequest request,
                ICommandHandler<Command, Result<GameVersionResponse>> handler,
                CancellationToken cancellationToken) =>
            {
                Command command = new(request.Version);

                Result<GameVersionResponse> result = await handler.Handle(command, cancellationToken);

                // Point Location at the new resource's own item endpoint (GET /game-versions/{id}, added in M2-25).
                return result.IsSuccess
                    ? Results.Created($"/api/v1/game-versions/{result.Value.Id.Value}", result.Value)
                    : Results.Problem(result.Error.ToProblemDetails());
            })
            .WithName(nameof(RegisterGameVersion))
            .WithTags("GameVersions")
            .RequireAuthorization(AuthorizationPolicies.RequireAdminRole)
            .Produces<GameVersionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }
}
