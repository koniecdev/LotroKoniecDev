namespace LotroKoniecDev.Tests.Shared;

/// <summary>
/// The <c>source_digest</c> parity fixture of ADR-0047 §6: the one place where both contexts' digest
/// code is checked against values neither of them produced.
/// </summary>
/// <remarks>
/// <para>
/// The patcher's <c>SourceDigest</c> and the TMS' <c>SourceHash</c> implement one wire contract
/// separately. Their own suites would both stay green through a one-sided change to the framing, the
/// truncation or the byte order, and every row on every player's box would then report
/// <c>source moved</c> after a deploy. This file is linked into <c>Tests.Unit</c> and
/// <c>TranslationSystem.Domain.Tests.Unit</c>, so a drift fails a build instead.
/// </para>
/// <para>
/// The expected values are computed outside both implementations, from the framing as ADR-0047 states
/// it: per field, <c>marker | UTF-16 code-unit count (little-endian int32) | UTF-16LE bytes</c>, an
/// absent field being the single marker byte <c>0</c>; SHA-256 of the concatenation; the first eight
/// bytes in digest order, hex, lower-cased. Never regenerate them from either implementation.
/// </para>
/// <para>
/// The cases target what the framing can get wrong: args that are absent, present or empty, the piece
/// placeholder, real newlines and backslashes (the ADR-0039 escape is undone before a digest is taken),
/// a surrogate pair, an empty text, and the <c>("ab","c")</c> / <c>("a","bc")</c> pair that only the
/// length framing keeps apart.
/// </para>
/// </remarks>
public static class SourceDigestGoldenCases
{
    public static TheoryData<string, string?, string?, string> All =>
        new()
        {
            { "Witaj w Srodziemiu!", null, null, "a37cc1683216cd32" },
            { "Tekst z <--DO_NOT_TOUCH!--> argumentem", "1", "1", "eacc6a53f9a2ae91" },
            { "Masz <--DO_NOT_TOUCH!--> zlota i <--DO_NOT_TOUCH!--> srebra.", "1-2", "1-2", "efd5d8d264a9eb18" },
            { "Wiersz jeden\nWiersz dwa\r\nWiersz trzy", null, null, "42a0c7ecf85b56e4" },
            { @"Sciezka C:\notes i sekwencja \n", null, null, "23e21ca43b96da7f" },
            { "", null, null, "847b68e04850722a" },
            { "Zażółć gęślą jaźń 🧙", null, null, "b2f3afcca46663fc" },
            { "ab", "c", null, "a51a19895c069e97" },
            { "a", "bc", null, "9ccfe82c2b86ec80" },
            { "x", "", null, "c0f4ae6c3e8cde9c" },
        };
}
