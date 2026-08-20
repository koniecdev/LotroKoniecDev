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
/// Serves the ready-made translation file for a language (spec 0001). It sends the stored artifact
/// with its content hash as the <c>ETag</c> and answers 304 to a matching <c>If-None-Match</c>. It is
/// open to anyone, because the CLI and the players download it.
/// It never builds the file per request. After each write that matters,
/// <see cref="IPrecomputedTranslationFileProjector"/> rebuilds it in the background with a short delay
/// (PERF-04, ADR-0021), so a download can lag a commit by a moment.
/// The 304 decision needs only the hash (PERF-01, #286). PostgreSQL stores the multi-MB
/// <c>Content</c> column out of line, so a revalidation that never reads it costs the same whatever
/// the artifact size. The content is fetched only when the client's ETag no longer matches.
/// The ETag is also the integrity hash (AUDIT-SEC-01, #391): the patcher computes the hex SHA-256 of
/// the downloaded UTF-8 body and refuses the file when it differs. So the hash algorithm and the
/// strong-ETag format are a contract between the two contexts. Change them only together with the
/// patcher.
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

                // If-None-Match is a comma-separated list and may be "*" (RFC 9110 §13.1.2). We answer
                // 304 when any of the values matches the current strong tag.
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

                // The ETag comes from the same row read as the content, so even if a rebuild lands
                // between the hash lookup and this read, the client still gets a matching tag and body.
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
    /// Clients keep the file they have and ask again with <c>If-None-Match</c>. Setting Cache-Control
    /// here stops GlobalNoCacheMiddleware from adding no-store.
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
