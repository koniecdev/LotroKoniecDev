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
    /// <paramref name="email"/> comes from the login page's "account not confirmed" message (ADR-0046) and
    /// only fills in the form; the POST still hides whether an account exists.
    /// </summary>
    public void OnGet(string? email)
    {
        IsSubmitted = false;
        Email = email?.Trim() ?? string.Empty;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        // The handler hides whether an account exists (ADR-0059) and succeeds for every well-formed
        // address, so only a malformed one comes back as a failure.
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
