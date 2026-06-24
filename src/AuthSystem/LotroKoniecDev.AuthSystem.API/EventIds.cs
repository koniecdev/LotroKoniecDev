namespace LotroKoniecDev.AuthSystem.API;

/// <summary>
/// Event ID ranges: TranslationSystem 1000–1999, AuthSystem 2000–2999, Shared 3000–3999.
/// </summary>
internal static class EventIds
{
    // Exception Handlers (2100–2199)
    public const int ArgumentException = 2100;
    public const int BadHttpRequest = 2110;
    public const int ConcurrencyConflict = 2120;
    public const int ValidationFailure = 2140;
    public const int UnhandledException = 2150;

    // Change Password (2200–2209)
    public const int ChangePasswordSecurityStampFailed = 2200;
    public const int ChangePasswordFailed = 2201;

    // Email Confirmation (2210–2219)
    public const int EmailConfirmationFailed = 2210;

    // GDPR Erasure (2220–2239)
    public const int GdprErasureInitiated = 2220;
    public const int GdprErasureAnonymizationFailed = 2223;
    public const int GdprErasureAuthAnonymized = 2224;
    public const int GdprErasureAuthFailed = 2225;
    public const int GdprErasureAccountDeleted = 2226;
    public const int GdprErasureArtifactsCleaned = 2227;
    public const int GdprErasureArtifactsCleanupFailed = 2228;
    public const int GdprErasureEmergencyLockout = 2229;
    public const int GdprErasureEmergencyLockoutFailed = 2230;

    // Data Export (2240–2249)
    public const int ExportDataCompleted = 2241;

    // Forgot Password (2250–2259)
    public const int ForgotPasswordNonExistent = 2250;
    public const int ForgotPasswordEmailFailed = 2251;

    // Logout (2260–2269)
    public const int UserLoggedOut = 2260;

    // Registration (2270–2279)
    public const int RegisterEmailFallback = 2270;
    public const int RegisterConcurrentRace = 2271;

    // Resend Confirmation (2280–2289)
    public const int ResendConfirmNonExistent = 2280;
    public const int ResendConfirmAlreadyConfirmed = 2281;
    public const int ResendConfirmEmailFailed = 2282;

    // Reset Password (2290–2299)
    public const int ResetPasswordFailed = 2290;
    public const int ResetPasswordSecurityStampFailed = 2291;

    // Token Revocation (2300–2309)
    public const int TokenRevoked = 2300;

    // Middleware (2400–2499)
    public const int UnauthorizedAccessAttempt = 2400;
    public const int ForbiddenAccessAttempt = 2401;

    // Pages (2600–2699)
    public const int EmailConfirmedViaUi = 2600;
    public const int PasswordResetTokenGenerated = 2610;
    public const int LoginUserNotFound = 2620;
    public const int LoginAccountLockedOut = 2621;
    public const int LoginWrongPassword = 2622;
    public const int LoginSuccessful = 2623;
    public const int PasswordResetCompletedViaUi = 2630;
    public const int RegisterCompletedViaUi = 2640;
}
