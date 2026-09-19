using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.Persistence.Identity;

namespace LotroKoniecDev.AuthSystem.API.Services.Accounts;

/// <inheritdoc />
internal sealed class EmailChangeRevertWindow : IEmailChangeRevertWindow
{
    private readonly EmailChangeRevertTokenProviderOptions _revertTokenOptions;

    public EmailChangeRevertWindow(IOptions<EmailChangeRevertTokenProviderOptions> revertTokenOptions)
    {
        _revertTokenOptions = revertTokenOptions.Value;
    }

    public DateTimeOffset? ExpiresAt(DateTimeOffset? armedAt) =>
        armedAt is { } armed ? armed + _revertTokenOptions.TokenLifespan : null;

    // A null expiry loses this comparison, which is the answer we want: nothing armed is nothing live.
    public bool IsLiveAt(DateTimeOffset? armedAt, DateTimeOffset moment) => ExpiresAt(armedAt) > moment;

    public DateTimeOffset LiveSince(DateTimeOffset moment) => moment - _revertTokenOptions.TokenLifespan;
}
