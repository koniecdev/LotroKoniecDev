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
    /// <paramref name="email"/> comes from the login page's "account not confirmed" message
    /// (ADR-0046). It only fills in the form. The POST handler still hides whether an account exists,
    /// so an address typed into the query string reveals no more than one typed into the field.
    /// </summary>
    public void OnGet(string? email)
    {
        IsSubmitted = false;
        Email = email?.Trim() ?? string.Empty;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        // The handler is what hides whether an account exists: it looks the user up, does the same
        // work when there is none, does nothing when the address is already confirmed, and otherwise
        // creates a token and sends. It always succeeds for a well-formed address. Only an address
        // that is not well formed, and so can match no account, comes back as a failure worth showing.
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
