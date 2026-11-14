using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Services.Accounts;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

internal static class IdentityResultExtensions
{
    extension(IdentityResult result)
    {
        /// <summary>
        /// <c>UpdateAsync</c> looks the address up again before it writes, so an e-mail change can lose the
        /// race there too, not only at the unique index (#866). Only a refusal whose every error is a taken
        /// address counts; any other error must reach the log as a failure.
        /// </summary>
        public bool IsTakenEmail =>
            result.Errors.Any()
            && result.Errors.All(error => error.Code is nameof(IdentityErrorDescriber.DuplicateEmail));

        /// <summary>
        /// True only when the breach check is the sole reason (ADR-0066): any other error has a message
        /// the caller must not hide.
        /// </summary>
        public bool IsBreachedPassword =>
            result.Errors.Any()
            && result.Errors.All(error => error.Code is BreachedPasswordValidator.ErrorCode);

        /// <summary>
        /// Any error with this code wins over the rest: the callers checked the reservation before Identity's
        /// own rules until #905, and their answers stay the same.
        /// </summary>
        public bool IsReservedEmail =>
            result.Errors.Any(error => error.Code is EmailChangeRevertReservationValidator.ErrorCode);
    }
}
