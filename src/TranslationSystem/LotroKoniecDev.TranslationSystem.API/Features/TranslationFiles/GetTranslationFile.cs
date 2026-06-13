using System.Text;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// Serves the pre-built translation file for a language (spec 0001): streams the stored artifact
/// with its content hash as the <c>ETag</c> and honors <c>If-None-Match</c> with a 304. Anonymous
/// (the CLI/player downloads it). Never builds per-request — the artifact is regenerated on write
/// by <see cref="ITranslationArtifactBuilder"/>.
/// </summary>
internal sealed class GetTranslationFile : IEndpoint
{
    private const string SupportedLanguage = SupportedLanguages.Polish;

    internal sealed record Query(string Lang) : IQuery<Result<TranslationFileResult>>;

    internal sealed record TranslationFileResult(string Content, string ETag);

    internal sealed class Handler : IQueryHandler<Query, Result<TranslationFileResult>>
    {
        private readonly IApplicationReadDbContext _readDbContext;

        public Handler(IApplicationReadDbContext readDbContext)
        {
            _readDbContext = readDbContext;
        }

        public async ValueTask<Result<TranslationFileResult>> Handle(Query query, CancellationToken cancellationToken)
        {
            if (!string.Equals(query.Lang, SupportedLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure<TranslationFileResult>(new Error(
                    "TranslationFiles.UnsupportedLanguage",
                    $"Language '{query.Lang}' is not supported; only '{SupportedLanguage}' exists today.",
                    TypeOfError.Validation));
            }

            TranslationFileResult? result = await _readDbContext.TranslationArtifacts
                .Where(artifact => artifact.Language == SupportedLanguage)
                .Select(artifact => new TranslationFileResult(artifact.Content, artifact.ContentHash))
                .FirstOrDefaultAsync(cancellationToken);

            return result is null
                ? Result.Failure<TranslationFileResult>(new Error(
                    "TranslationFiles.NotFound",
                    $"No translation file has been built for '{SupportedLanguage}' yet.",
                    TypeOfError.NotFound))
                : Result.Success(result);
        }
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapGet("/api/v1/translation-files/{lang}", async (
                string lang,
                IQueryHandler<Query, Result<TranslationFileResult>> handler,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                Result<TranslationFileResult> result = await handler.Handle(new Query(lang), cancellationToken);

                if (result.IsFailure)
                {
                    return Results.Problem(result.Error.ToProblemDetails());
                }

                EntityTagHeaderValue entityTag = new($"\"{result.Value.ETag}\"");

                // Revalidation model: clients keep the cached file and re-check via If-None-Match.
                // Setting Cache-Control explicitly stops GlobalNoCacheMiddleware from stamping no-store.
                httpContext.Response.Headers.CacheControl = "private, no-cache";
                httpContext.Response.GetTypedHeaders().ETag = entityTag;

                // If-None-Match is a comma-separated list and may be "*" (RFC 9110 §13.1.2):
                // 304 when any supplied validator matches the current strong tag.
                bool notModified = httpContext.Request.GetTypedHeaders().IfNoneMatch
                    .Any(candidate => candidate.Equals(EntityTagHeaderValue.Any)
                                      || candidate.Compare(entityTag, useStrongComparison: true));

                return notModified
                    ? Results.StatusCode(StatusCodes.Status304NotModified)
                    : Results.Text(result.Value.Content, "text/plain", Encoding.UTF8);
            })
            .WithName(nameof(GetTranslationFile))
            .WithTags("TranslationFiles")
            .AllowAnonymous()
            .Produces<string>(StatusCodes.Status200OK, "text/plain")
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
