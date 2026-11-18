using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.Cli;

/// <summary>
/// The risk note for players (spec 0011, LEGAL-12) that <c>patch</c> and <c>launch</c> print. It only
/// informs. It never asks a question and never changes behaviour or exit codes.
/// </summary>
internal static class RiskNotice
{
    public const string Text = PlayerNoticeConstants.Risk;
}
