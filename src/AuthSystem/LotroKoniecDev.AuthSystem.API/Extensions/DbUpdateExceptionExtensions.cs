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
        public bool IsUniqueViolation =>
            exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

        /// <summary>
        /// Registration and both legs of an e-mail change check that a value is free with a plain query,
        /// so two requests can pass that check at the same moment, and then the unique index decides. The
        /// loser gets the answer the check would have given it. Any other save error is not a taken value,
        /// so this returns null and the error goes on up (#845, #864). The three handlers share this one
        /// copy so they cannot drift apart on what counts as taken.
        /// </summary>
        public Error? TakenAccountValueError(IModel model)
        {
            if (exception.InnerException is not PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } violation)
            {
                return null;
            }

            IIndex? index = model.FindEntityType(typeof(ApplicationUser))?
                .GetIndexes()
                .FirstOrDefault(i => string.Equals(
                    i.GetDatabaseName(), violation.ConstraintName, StringComparison.Ordinal));

            // Only a single-column index proves that the one value is taken. An index over more columns
            // would clash on the combination, not on the address or the name alone.
            return index?.Properties switch
            {
                [{ Name: nameof(ApplicationUser.Email) or nameof(ApplicationUser.NormalizedEmail) }] =>
                    AuthErrors.UserAlreadyExistsByEmail,
                [{ Name: nameof(ApplicationUser.UserName) or nameof(ApplicationUser.NormalizedUserName) }] =>
                    AuthErrors.UserAlreadyExistsByUsername,
                _ => null
            };
        }
    }
}
