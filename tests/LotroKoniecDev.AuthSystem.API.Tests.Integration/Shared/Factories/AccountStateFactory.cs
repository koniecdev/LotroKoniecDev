using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;

/// <summary>
/// Puts a registered account into the state a branch needs. The account states live in the shared
/// database, so any host's services will do for them. The budgets live in each host's memory, so the
/// budget methods need the services of the host that will answer.
/// </summary>
internal static class AccountStateFactory
{
    public static async Task LockOutAsync(IServiceProvider services, string email)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = await FindAsync(userManager, email);

        await userManager.SetLockoutEnabledAsync(user, true);
        await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(30));
    }

    /// <summary>
    /// Changes only the stamp and revokes nothing, the state a session-ending flow leaves when its revoke fails.
    /// </summary>
    public static async Task ChangeSecurityStampAsync(IServiceProvider services, string email)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = await FindAsync(userManager, email);

        IdentityResult result = await userManager.UpdateSecurityStampAsync(user);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Could not change the security stamp of test user '{email}'.");
        }
    }

    public static async Task RemovePasswordAsync(IServiceProvider services, string email)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = await FindAsync(userManager, email);

        IdentityResult result = await userManager.RemovePasswordAsync(user);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Could not remove the password of test user '{email}'.");
        }
    }

    /// <summary>
    /// Only the field the branches read: each of them checks <c>DeletionScheduledAt</c> before anything else.
    /// </summary>
    public static async Task ScheduleDeletionAsync(IServiceProvider services, string email)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = await FindAsync(userManager, email);

        user.DeletionScheduledAt = DateTimeOffset.UtcNow;
        IdentityResult result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Could not schedule the deletion of test user '{email}'.");
        }
    }

    /// <summary>
    /// Moves a scheduled deletion <paramref name="age"/> into the past and leaves the security stamp
    /// alone, so the cancel token already mailed still verifies. That is the state of a cancel e-mail
    /// that went out late (#947).
    /// </summary>
    public static async Task BackdateDeletionScheduleAsync(IServiceProvider services, string email, TimeSpan age)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = await FindAsync(userManager, email);

        user.DeletionScheduledAt = DateTimeOffset.UtcNow - age;
        IdentityResult result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Could not backdate the deletion of test user '{email}'.");
        }
    }

    /// <summary>
    /// Deletes the row the way an operator does by hand: nothing is revoked, so every token the account
    /// already holds is still valid in the database.
    /// </summary>
    public static async Task DeleteAsync(IServiceProvider services, string email)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = await FindAsync(userManager, email);

        IdentityResult result = await userManager.DeleteAsync(user);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Could not delete test user '{email}'.");
        }
    }

    public static async Task SpendPasswordResetBudgetAsync(IServiceProvider services, string email)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = await FindAsync(userManager, email);
        IPasswordResetRequestThrottle throttle = services.GetRequiredService<IPasswordResetRequestThrottle>();

        SpendUntilRefused(() => throttle.TryAcquire(user), AccountBudgets.PasswordResetPermitLimit);
    }

    public static async Task SpendConfirmationResendBudgetAsync(IServiceProvider services, string email)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = await FindAsync(userManager, email);
        IEmailConfirmationResendThrottle throttle = services.GetRequiredService<IEmailConfirmationResendThrottle>();
        MailboxKey mailbox = MailboxKey.FromNormalizedEmail(user.NormalizedEmail);

        SpendUntilRefused(() => throttle.TryAcquire(mailbox), AccountBudgets.EmailConfirmationResendPermitLimit);
    }

    public static async Task SpendDeletionScheduledLoginBudgetAsync(IServiceProvider services, string email)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = await FindAsync(userManager, email);
        IDeletionScheduledLoginThrottle throttle = services.GetRequiredService<IDeletionScheduledLoginThrottle>();

        SpendUntilRefused(() => throttle.TryAcquire(user.Id), AccountBudgets.DeletionScheduledLoginPermitLimit);
    }

    /// <summary>
    /// Registration keys its budget on the typed address, whether or not it has an account (#860).
    /// </summary>
    public static void SpendRegistrationBudget(IServiceProvider services, string email)
    {
        using IServiceScope scope = services.CreateScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        IRegistrationMailboxThrottle throttle = services.GetRequiredService<IRegistrationMailboxThrottle>();
        MailboxKey mailbox = MailboxKey.FromNormalizedEmail(userManager.NormalizeEmail(email));

        SpendUntilRefused(() => throttle.TryAcquire(mailbox), AccountBudgets.RegistrationPermitLimit);
    }

    /// <summary>
    /// Reserves <paramref name="reservedEmail"/> as the undo target of the account on <paramref name="email"/>,
    /// the state an e-mail change leaves behind (#684), without running the change.
    /// </summary>
    public static async Task ReserveAddressAsync(IServiceProvider services, string email, string reservedEmail)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = await FindAsync(userManager, email);

        user.ArmEmailChangeRevert(reservedEmail, userManager.NormalizeEmail(reservedEmail), DateTimeOffset.UtcNow);

        IdentityResult result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Could not reserve an address for test user '{email}'.");
        }
    }

    /// <summary>
    /// Takes permits until the budget refuses one, so a "budget spent" test really reaches that branch.
    /// Throws when the budget still has permits after its whole limit.
    /// </summary>
    private static void SpendUntilRefused(Func<bool> tryAcquire, int permitLimit)
    {
        for (int permit = 0; permit <= permitLimit; permit++)
        {
            if (!tryAcquire())
            {
                return;
            }
        }

        throw new InvalidOperationException($"The budget still had permits after {permitLimit + 1} tries.");
    }

    private static async Task<ApplicationUser> FindAsync(UserManager<ApplicationUser> userManager, string email) =>
        await userManager.FindByEmailAsync(email)
            ?? throw new InvalidOperationException($"Test user '{email}' was not found.");
}
