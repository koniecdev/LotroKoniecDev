using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Services.Accounts;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

internal static class IdentityResultExtensions
{
    extension(IdentityResult result)
    {
        /// <summary>
        /// <c>UpdateAsync</c> looks the address up once more before it writes, so an e-mail change can lose
        /// the race for its address there too, not only at the unique index (#866). Only a refusal whose
        /// every error is a taken address counts. With any other error the save would fail on a free
        /// address as well, so it must reach the log as the failure it is.
        /// </summary>
        public bool IsTakenEmail =>
            result.Errors.Any()
            && result.Errors.All(error => error.Code is nameof(IdentityErrorDescriber.DuplicateEmail));

        /// <summary>
        /// Identity refused the new password because it is in a known data breach (ADR-0065). It wins over
        /// any other refusal in the same result: the user has to pick a new password either way.
        /// </summary>
        public bool IsBreachedPassword =>
            result.Errors.Any(error => error.Code is BreachedPasswordValidator.ErrorCode);
    }
}
