using Microsoft.AspNetCore.Mvc;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;

namespace LotroKoniecDev.TranslationSystem.API.Extensions;

internal static class ErrorExtensions
{
    extension(Error error)
    {
        public ProblemDetails ToProblemDetails()
        {
            ProblemDetails response = error.Type switch
            {
                TypeOfError.NotFound => error.CreateProblemDetails(StatusCodes.Status404NotFound),
                TypeOfError.Validation => error.CreateProblemDetails(StatusCodes.Status400BadRequest),
                TypeOfError.Forbidden => error.CreateProblemDetails(StatusCodes.Status403Forbidden),
                TypeOfError.DataConflict => error.CreateProblemDetails(StatusCodes.Status422UnprocessableEntity),
                _ => error.CreateProblemDetails(StatusCodes.Status500InternalServerError)
            };
            return response;
        }

        private ProblemDetails CreateProblemDetails(int statusCode) =>
            new()
            {
                Status = statusCode,
                Type = $"https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/{statusCode}",
                Title = Error.GetTitle(error.Type),
                Detail = error.Message,
                Extensions = { ["errorCode"] = error.Code }
            };

        private static string GetTitle(TypeOfError type) =>
            type switch
            {
                TypeOfError.NotFound => "Not Found",
                TypeOfError.Validation => "Validation Error",
                TypeOfError.DataConflict => "Data Conflict",
                TypeOfError.Forbidden => "Forbidden",
                TypeOfError.Failure => "Internal Server Error",
                _ => "Error"
            };
    }
}
