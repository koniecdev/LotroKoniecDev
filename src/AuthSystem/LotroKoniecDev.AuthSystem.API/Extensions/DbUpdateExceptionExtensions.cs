using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.BuildingBlocks;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

internal static class DbUpdateExceptionExtensions
{
    extension(DbUpdateException exception)
    {
        public string? ViolatedUniqueConstraint => exception.UniqueViolation?.ConstraintName;

        /// <summary>
        /// Two requests can pass the "is this value free" query at once, and then the unique index
        /// decides; the loser gets the answer the query would have given it (#845, #864). Any other save
        /// error returns null and goes on up. Registration and both e-mail change legs share this copy.
        /// </summary>
        public Error? TakenAccountValueError(IModel model)
        {
            if (exception.UniqueViolation is not { } violation)
            {
                return null;
            }

            IIndex? index = model.FindEntityType(typeof(ApplicationUser))?
                .GetIndexes()
                .FirstOrDefault(i => string.Equals(
                    i.GetDatabaseName(), violation.ConstraintName, StringComparison.Ordinal));

            // Only a single-column index proves that the one value is taken.
            return index?.Properties switch
            {
                [{ Name: nameof(ApplicationUser.Email) or nameof(ApplicationUser.NormalizedEmail) }] =>
                    AuthErrors.UserAlreadyExistsByEmail,
                [{ Name: nameof(ApplicationUser.UserName) or nameof(ApplicationUser.NormalizedUserName) }] =>
                    AuthErrors.UserAlreadyExistsByUsername,
                _ => null
            };
        }

        /// <summary>
        /// An e-mail change never picks a new username, so a clash on the username index is not a race it
        /// lost. Only a taken address is.
        /// </summary>
        public bool IsTakenEmail(IModel model) =>
            exception.TakenAccountValueError(model) == AuthErrors.UserAlreadyExistsByEmail;

        private PostgresException? UniqueViolation =>
            exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } violation
                ? violation
                : null;
    }
}
