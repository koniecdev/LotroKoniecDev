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
/// by <see cref="IPrecomputedTranslationFileProjector"/>. The 304 decision is driven by a
/// hash-only lookup (PERF-01/#286): the multi-MB <c>Content</c> column is TOASTed by PostgreSQL,
/// so a revalidation that never reads it stays O(1) regardless of artifact size — content is
/// fetched only when the client's validator no longer matches.
/// </summary>
internal sealed class GetTranslationFile : IEndpoint
{
    private const string SupportedLanguage = SupportedLanguages.Polish;

    internal sealed record HashQuery(string Lang) : IQuery<Result<string>>;

    internal sealed record Query(string Lang) : IQuery<Result<TranslationFileResult>>;

    internal sealed record TranslationFileResult(string Content, string ETag);

    internal sealed class HashHandler : IQueryHandler<HashQuery, Result<string>>
    {
        private readonly IApplicationReadDbContext _readDbContext;

        public HashHandler(IApplicationReadDbContext readDbContext)
        {
            _readDbContext = readDbContext;
        }

        public async ValueTask<Result<string>> Handle(HashQuery query, CancellationToken cancellationToken)
        {
            if (ValidateLanguage(query.Lang) is { } validationError)
            {
                return Result.Failure<string>(validationError);
            }

            string? contentHash = await _readDbContext.PrecomputedTranslationFiles
                .Where(file => file.Language == SupportedLanguage)
                .Select(file => file.ContentHash)
                .FirstOrDefaultAsync(cancellationToken);

            return contentHash is null
                ? Result.Failure<string>(NotFound())
                : Result.Success(contentHash);
        }
    }

    internal sealed class Handler : IQueryHandler<Query, Result<TranslationFileResult>>
    {
        private readonly IApplicationReadDbContext _readDbContext;

        public Handler(IApplicationReadDbContext readDbContext)
        {
            _readDbContext = readDbContext;
        }

        public async ValueTask<Result<TranslationFileResult>> Handle(Query query, CancellationToken cancellationToken)
        {
            if (ValidateLanguage(query.Lang) is { } validationError)
            {
                return Result.Failure<TranslationFileResult>(validationError);
            }

            TranslationFileResult? result = await _readDbContext.PrecomputedTranslationFiles
                .Where(file => file.Language == SupportedLanguage)
                .Select(file => new TranslationFileResult(file.Content, file.ContentHash))
                .FirstOrDefaultAsync(cancellationToken);

            return result is null
                ? Result.Failure<TranslationFileResult>(NotFound())
                : Result.Success(result);
        }
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapGet("/api/v1/translation-files/{lang}", async (
                string lang,
                IQueryHandler<HashQuery, Result<string>> hashHandler,
                IQueryHandler<Query, Result<TranslationFileResult>> handler,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                Result<string> hashResult = await hashHandler.Handle(new HashQuery(lang), cancellationToken);

                if (hashResult.IsFailure)
                {
                    return Results.Problem(hashResult.Error.ToProblemDetails());
                }

                EntityTagHeaderValue entityTag = new($"\"{hashResult.Value}\"");

                // If-None-Match is a comma-separated list and may be "*" (RFC 9110 §13.1.2):
                // 304 when any supplied validator matches the current strong tag.
                bool notModified = httpContext.Request.GetTypedHeaders().IfNoneMatch
                    .Any(candidate => candidate.Equals(EntityTagHeaderValue.Any)
                                      || candidate.Compare(entityTag, useStrongComparison: true));

                if (notModified)
                {
                    SetRevalidationHeaders(httpContext, entityTag);
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                Result<TranslationFileResult> result = await handler.Handle(new Query(lang), cancellationToken);

                if (result.IsFailure)
                {
                    return Results.Problem(result.Error.ToProblemDetails());
                }

                // The ETag ships from the same row read as the content: were a rebuild to land between
                // the hash lookup and this fetch, the client still receives a matching (tag, body) pair.
                SetRevalidationHeaders(httpContext, new EntityTagHeaderValue($"\"{result.Value.ETag}\""));
                return Results.Text(result.Value.Content, "text/plain", Encoding.UTF8);
            })
            .WithName(nameof(GetTranslationFile))
            .WithTags("TranslationFiles")
            .AllowAnonymous()
            .Produces<string>(StatusCodes.Status200OK, "text/plain")
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Revalidation model: clients keep the cached file and re-check via <c>If-None-Match</c>.
    /// Setting Cache-Control explicitly stops GlobalNoCacheMiddleware from stamping no-store.
    /// </summary>
    private static void SetRevalidationHeaders(HttpContext httpContext, EntityTagHeaderValue entityTag)
    {
        httpContext.Response.Headers.CacheControl = "private, no-cache";
        httpContext.Response.GetTypedHeaders().ETag = entityTag;
    }

    private static Error? ValidateLanguage(string lang)
        => string.Equals(lang, SupportedLanguage, StringComparison.OrdinalIgnoreCase)
            ? null
            : new Error(
                "TranslationFiles.UnsupportedLanguage",
                $"Language '{lang}' is not supported; only '{SupportedLanguage}' exists today.",
                TypeOfError.Validation);

    private static Error NotFound()
        => new(
            "TranslationFiles.NotFound",
            $"No translation file has been built for '{SupportedLanguage}' yet.",
            TypeOfError.NotFound);
}
