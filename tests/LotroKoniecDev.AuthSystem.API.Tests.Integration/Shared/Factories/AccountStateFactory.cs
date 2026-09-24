using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;

/// <summary>
/// Puts a registered account into the state a branch needs. The account states live in the shared
/// database, so any host's services will do for them. The mail budgets live in each host's memory, so the
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
