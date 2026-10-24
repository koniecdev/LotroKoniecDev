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
/// Serves the pre-built translation file (spec 0001), open to anyone because the CLI downloads it. The
/// background rebuild runs after a short delay (PERF-04, ADR-0021), so a download can lag a commit.
/// The 304 decision reads only the hash, never the out-of-line <c>Content</c> column (PERF-01), and a
/// full body streams from the disk copy (PERF-09, ADR-0064). The ETag is also the integrity hash the
/// patcher checks (AUDIT-SEC-01): change the hash algorithm or the strong-ETag format only together
/// with the patcher.
/// </summary>
internal sealed class GetTranslationFile : IEndpoint
{
    private const string SupportedLanguage = SupportedLanguages.Polish;

    /// <summary>
    /// The CLI decodes the body by this charset.
    /// </summary>
    private const string TextPlainUtf8 = "text/plain; charset=utf-8";

    internal sealed record HashQuery(string Lang) : IQuery<Result<string>>;

    internal sealed record Query(string Lang, string ContentHash) : IQuery<Result<TranslationFileResult>>;

    internal sealed record TranslationFileResult(Stream Content, string ETag);

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
        private readonly ITranslationFileDiskCache _diskCache;

        public Handler(ITranslationFileDiskCache diskCache)
        {
            _diskCache = diskCache;
        }

        public async ValueTask<Result<TranslationFileResult>> Handle(Query query, CancellationToken cancellationToken)
        {
            if (ValidateLanguage(query.Lang) is { } validationError)
            {
                return Result.Failure<TranslationFileResult>(validationError);
            }

            TranslationFileCopy? copy = await _diskCache.OpenAsync(SupportedLanguage, query.ContentHash, cancellationToken);

            return copy is null
                ? Result.Failure<TranslationFileResult>(NotFound())
                : Result.Success(new TranslationFileResult(copy.Content, copy.ContentHash));
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

                // If-None-Match is a list and may be "*" (RFC 9110 §13.1.2).
                bool notModified = httpContext.Request.GetTypedHeaders().IfNoneMatch
                    .Any(candidate => candidate.Equals(EntityTagHeaderValue.Any)
                                      || candidate.Compare(entityTag, useStrongComparison: true));

                if (notModified)
                {
                    SetRevalidationHeaders(httpContext, entityTag);
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                Result<TranslationFileResult> result = await handler.Handle(
                    new Query(lang, hashResult.Value), cancellationToken);

                if (result.IsFailure)
                {
                    return Results.Problem(result.Error.ToProblemDetails());
                }

                // The tag of the copy actually sent, which is newer if a rebuild landed in between.
                SetRevalidationHeaders(httpContext, new EntityTagHeaderValue($"\"{result.Value.ETag}\""));
                return Results.Stream(result.Value.Content, TextPlainUtf8);
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
