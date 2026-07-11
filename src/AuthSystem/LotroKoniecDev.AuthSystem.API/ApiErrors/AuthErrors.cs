using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;

namespace LotroKoniecDev.AuthSystem.API.ApiErrors;

internal static class AuthErrors
{
    public static Error UserAlreadyExistsByEmail =>
        new("Auth.UserAlreadyExistsByEmail",
            "A user with this email address already exists.",
            TypeOfError.DataConflict);

    public static Error UserAlreadyExistsByUsername =>
        new("Auth.UserAlreadyExistsByUsername",
            "A user with this username already exists.",
            TypeOfError.DataConflict);

    public static Error RegistrationFailed(string details) =>
        new("Auth.RegistrationFailed",
            details,
            TypeOfError.Validation);

    public static Error UserNotFound =>
        new("Auth.UserNotFound",
            "User not found.",
            TypeOfError.NotFound);

    public static Error InvalidPasswordResetToken =>
        new("Auth.InvalidPasswordResetToken",
            "The password reset token is invalid or has expired.",
            TypeOfError.Validation);

    public static Error PasswordResetFailed(string details) =>
        new("Auth.PasswordResetFailed",
            details,
            TypeOfError.Validation);

    public static Error InvalidCurrentPassword =>
        new("Auth.InvalidCurrentPassword",
            "The current password is incorrect.",
            TypeOfError.Validation);

    public static Error PasswordChangeFailed(string details) =>
        new("Auth.PasswordChangeFailed",
            details,
            TypeOfError.Validation);

    public static Error InvalidEmailConfirmationToken =>
        new("Auth.InvalidEmailConfirmationToken",
            "The email confirmation token is invalid or has expired.",
            TypeOfError.Validation);

    public static Error EmailConfirmationFailed(string details) =>
        new("Auth.EmailConfirmationFailed",
            details,
            TypeOfError.Validation);

    public static Error AccountDeletionFailed(string details) =>
        new("Auth.AccountDeletionFailed",
            details,
            TypeOfError.Failure);

    public static Error DeletionAlreadyScheduled =>
        new("Auth.DeletionAlreadyScheduled",
            "Account deletion is already scheduled.",
            TypeOfError.DataConflict);

    public static Error DeletionSchedulingFailed =>
        new("Auth.DeletionSchedulingFailed",
            "Account deletion could not be scheduled. Your account remains unchanged. " +
            "Please try again later or contact support.",
            TypeOfError.Failure);

    public static Error InvalidCancelDeletionToken =>
        new("Auth.InvalidCancelDeletionToken",
            "The cancellation link is invalid or has expired.",
            TypeOfError.Validation);

    public static Error CancelDeletionFailed(string details) =>
        new("Auth.CancelDeletionFailed",
            details,
            TypeOfError.Failure);
}
