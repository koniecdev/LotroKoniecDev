using Microsoft.AspNetCore.Identity;

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
    }
}
