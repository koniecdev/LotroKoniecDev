using System.Text;
using LotroKoniecDev.Application.Parsers;
using LotroKoniecDev.TranslationSystem.API.Parsing;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Parsing;

/// <summary>
/// The two bounded contexts own independent parsers of the same <c>||</c> contract
/// (CLAUDE.md: "share a data contract, not code"). This is the contract's drift guard: it pins that
/// the patcher's <see cref="TranslationFileParser"/> and the TMS' <see cref="TranslationExportParser"/>
/// carve an export line identically. They diverge only by design — the patcher unescapes content and
/// converts the args columns to 0-indexed int arrays, the TMS keeps the verbatim exported
/// representation — so parity is asserted at the contract-field level: file id, gossip id, the
/// both-ends-anchored content, the args boundary, and the approved flag.
/// </summary>
public sealed class ParserContractParityTests
{
    [Theory]
    [InlineData("620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1")]
    [InlineData("620756992||1002||Tekst z <--DO_NOT_TOUCH!--> argumentem||1||1||1")]
    [InlineData("620756992||1003||Line with || inside the content||NULL||NULL||1")]
    [InlineData("620756992||1004||Multi||arg||content||1-2||1-2||1")]
    [InlineData("620756992||1005||Trailing approved zero||NULL||NULL||0")]
    [InlineData("100||200||leading||||NULL||NULL||1")]
    [InlineData("100||200||||trailing||NULL||NULL||1")]
    public async Task BothParsers_OnTheSameContractLine_ShouldAgreeOnEveryField(string line)
    {
        // Arrange — the patcher parser is per-line; the TMS parser is per-stream.
        LotroKoniecDev.Domain.Models.Translation patcher = new TranslationFileParser().ParseLine(line).Value;

        ParsedExport tmsExport = await ParseAsync(line);
        ParsedExportRow tms = tmsExport.Rows.ShouldHaveSingleItem();

        // Assert — these lines carry no escape sequences, so the patcher's unescape is a no-op and
        // the anchored content compares directly; the args boundary is checked by rebuilding the
        // verbatim args string from the patcher's int arrays.
        patcher.FileId.ShouldBe(tms.FileId);
        ((long)patcher.GossipId).ShouldBe(tms.GossipId);
        patcher.Content.ShouldBe(tms.Content);
        patcher.IsApproved.ShouldBe(tms.Approved);
        ReconstructArgs(patcher.ArgsOrder).ShouldBe(tms.ArgsOrder);
        ReconstructArgs(patcher.ArgsId).ShouldBe(tms.ArgsId);
    }

    private static async Task<ParsedExport> ParseAsync(string content)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));
        return await new TranslationExportParser().ParseAsync(stream, CancellationToken.None);
    }

    /// <summary>Rebuilds the verbatim args string the TMS keeps from the patcher's 0-indexed int array.</summary>
    private static string ReconstructArgs(int[]? args)
        => args is null ? "NULL" : string.Join('-', args.Select(value => value + 1));
}
