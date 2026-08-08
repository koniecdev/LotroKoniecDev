using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

[EnableRateLimiting("resend-confirmation-limit")]
internal sealed class ResendConfirmationModel : PageModel
{
    private readonly ICommandHandler<ResendEmailConfirmation.Command, Result> _handler;

    public ResendConfirmationModel(ICommandHandler<ResendEmailConfirmation.Command, Result> handler)
    {
        _handler = handler;
    }

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    public bool IsSubmitted { get; set; }

    /// <summary>
    /// <paramref name="email"/> arrives from the login page's unconfirmed-account alert (ADR-0046)
    /// and is a form default, nothing more: the POST handler still owns the anti-enumeration
    /// behaviour, so an address typed into the query by hand reveals exactly as much as one typed
    /// into the field.
    /// </summary>
    public void OnGet(string? email)
    {
        IsSubmitted = false;
        Email = email?.Trim() ?? string.Empty;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        // The handler owns the anti-enumeration logic (find user → dummy work when absent,
        // no-op when already confirmed, generate token + send otherwise) and always succeeds
        // for well-formed input, so account existence never leaks. Only a malformed address —
        // which cannot map to any account — comes back as a failure worth surfacing.
        Result result = await _handler.Handle(new ResendEmailConfirmation.Command(Email), cancellationToken);

        if (result.IsFailure)
        {
            ModelState.AddModelError(string.Empty, "Podaj prawidłowy adres e-mail.");
            return Page();
        }

        IsSubmitted = true;
        return Page();
    }
}
