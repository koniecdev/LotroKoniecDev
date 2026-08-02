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
    public const int GdprDeletionScheduled = 2231;
    public const int GdprDeletionCancelled = 2232;
    public const int GdprDeletionFinalized = 2233;
    public const int GdprCancelTokenInvalid = 2234;
    public const int GdprDeletionScheduledEmailFailed = 2235;
    public const int GdprDeletionCancelledEmailFailed = 2236;
    public const int GdprDeletionScheduleUnwound = 2237;
    public const int GdprDeletionFinalizerRunFailed = 2238;
    public const int GdprDeletionFinalizerUserFailed = 2239;

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

    // Token Revocation (2300–2309) — 2300 (TokenRevoked) retired with the dead RevokeEndpoint (#349)
    public const int UserSessionsRevoked = 2301;
    public const int UserSessionsRevocationFailed = 2302;

    // Token Pruning (2310–2319)
    public const int OpenIddictPruneCompleted = 2310;
    public const int OpenIddictPruneFailed = 2311;

    // Outbox Relay (2320–2329)
    public const int OutboxRelayPassFailed = 2320;
    public const int OutboxMessagePublishFailed = 2321;
    public const int OutboxMessageUnroutable = 2322;

    // Email Confirmation Consumer (2330–2339)
    public const int EmailConsumerConnectFailed = 2330;
    public const int EmailConsumerStarted = 2331;
    public const int EmailConsumerPoisonMessage = 2332;
    public const int EmailConsumerTransientFailure = 2333;
    public const int EmailConsumerUnexpectedError = 2334;
    public const int EmailConfirmationUserGone = 2335;
    public const int EmailConfirmationAlreadyConfirmed = 2336;
    public const int EmailConfirmationAddressMissing = 2337;
    public const int EmailConsumerTeardownWarning = 2338;

    // Startup (2350–2359)
    public const int StartupTransientDatabaseFailure = 2350;

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
    public const int LoginEmailNotConfirmed = 2624;
    public const int LoginDeletionScheduled = 2625;
    public const int PasswordResetCompletedViaUi = 2630;
    public const int RegisterCompletedViaUi = 2640;
    public const int DeletionCancelledViaUi = 2650;

    // GDPR Deletion Scheduling internals (2700–2709)
    public const int GdprDeletionSchedulingUpdateFailed = 2700;
    public const int GdprDeletionScheduleUnwindFailed = 2701;
    public const int GdprDeletionScheduleUnwindException = 2702;
    public const int GdprDeletionScheduleStampFailed = 2703;
    public const int GdprDeletionScheduleArtifactRevocationFailed = 2704;
    public const int GdprDeletionCancelStampFailed = 2705;

    // Forgot/Reset Password deletion-window gates (2710–2719)
    public const int ForgotPasswordDeletionScheduled = 2710;
    public const int ResetPasswordDeletionScheduled = 2711;
}
