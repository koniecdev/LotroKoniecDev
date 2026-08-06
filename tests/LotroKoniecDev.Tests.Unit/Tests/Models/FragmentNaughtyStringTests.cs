using System.Text;
using LotroKoniecDev.Domain.Core.Utilities;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Tests.Shared;

namespace LotroKoniecDev.Tests.Unit.Tests.Models;

/// <summary>
/// Hostile-input coverage for the binary half of the patcher (#569): a fragment's UTF-16 pieces and
/// argument strings are length-prefixed with <see cref="VarLenEncoder"/>, so anything that makes a
/// character count disagree with its byte count — surrogate pairs, combining marks, astral-plane
/// characters — corrupts every following fragment in the subfile, not just its own.
/// </summary>
public sealed class FragmentNaughtyStringTests
{
    private const int VarLenSingleByteCeiling = 127;
    private const int VarLenTwoByteCeiling = 32767;

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public void WriteThenParse_NaughtyPiece_ShouldRoundTripExactly(string naughty)
    {
        // Arrange
        Fragment fragment = new() { Pieces = [naughty] };

        // Act
        Fragment parsed = RoundTrip(fragment);

        // Assert
        parsed.Pieces.ShouldBe([naughty]);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.UnicodeHazards), MemberType = typeof(NaughtyStringCases))]
    public void WriteThenParse_NaughtyPiecesSplitAcrossOneFragment_ShouldRoundTripExactly(string naughty)
    {
        // Arrange — the game splits a single displayed text into several pieces; a length error in
        // one of them desynchronises the reader for the rest of the fragment.
        Fragment fragment = new() { Pieces = [naughty, string.Empty, naughty, "plain", naughty] };

        // Act
        Fragment parsed = RoundTrip(fragment);

        // Assert
        parsed.Pieces.ShouldBe([naughty, string.Empty, naughty, "plain", naughty]);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.UnicodeHazards), MemberType = typeof(NaughtyStringCases))]
    public void WriteThenParse_NaughtyArgumentStrings_ShouldRoundTripExactly(string naughty)
    {
        // Arrange — argument string groups carry the same VarLen + UTF-16 encoding as pieces and are
        // read after the argument references, so they desynchronise the reader just as badly.
        Fragment fragment = new() { Pieces = ["Text with a placeholder"] };
        fragment.ArgRefs.Add([0x01, 0x00, 0x00, 0x00]);
        fragment.ArgStrings.Add([naughty, string.Empty]);

        // Act
        Fragment parsed = RoundTrip(fragment);

        // Assert
        parsed.ArgStrings.ShouldHaveSingleItem().ShouldBe([naughty, string.Empty]);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.UnicodeHazards), MemberType = typeof(NaughtyStringCases))]
    public void WriteThenParse_NaughtyPieceRepeatedIntoATwoByteLength_ShouldRoundTripExactly(string naughty)
    {
        // Arrange — repeat until the length genuinely crosses the VarLen one-byte ceiling, so the
        // prefix takes the two-byte high-bit path while the payload stays hostile. A fixed repeat
        // count would not: a third of the hazard entries are one or two characters long. Repeating
        // whole copies never splits a surrogate pair.
        int repeats = VarLenSingleByteCeiling / naughty.Length + 1;
        string repeated = string.Concat(Enumerable.Repeat(naughty, repeats));
        Fragment fragment = new() { Pieces = [repeated] };

        // Act
        Fragment parsed = RoundTrip(fragment);

        // Assert
        repeated.Length.ShouldBeGreaterThan(VarLenSingleByteCeiling);
        parsed.Pieces.ShouldBe([repeated]);
    }

    [Fact]
    public void Write_PieceOfAstralPlaneCharacters_ShouldPrefixItWithItsUtf16CodeUnitCount()
    {
        // Arrange — the length prefix counts UTF-16 code units, NOT runes. These four emoji are
        // eight code units; a rune-counting writer would halve the declared length and shred every
        // following fragment in the subfile. The round-trip theories above would catch that too —
        // this states the wire rule outright, on the one input where runes and code units differ.
        const string naughty = "😀😀😀😀";
        Fragment fragment = new() { Pieces = [naughty] };
        using MemoryStream stream = new();

        using (BinaryWriter writer = new(stream, Encoding.Unicode, leaveOpen: true))
        {
            fragment.Write(writer);
        }

        stream.Position = sizeof(ulong) + sizeof(int); // past FragmentId and the piece count
        using BinaryReader reader = new(stream, Encoding.Unicode, leaveOpen: true);

        // Act
        int encodedLength = VarLenEncoder.Read(reader);

        // Assert
        encodedLength.ShouldBe(8);
    }

    [Theory]
    [InlineData(VarLenSingleByteCeiling)]
    [InlineData(VarLenSingleByteCeiling + 1)]
    [InlineData(VarLenTwoByteCeiling)]
    public void WriteThenParse_PieceAtAVarLenBoundary_ShouldRoundTripExactly(int length)
    {
        // Arrange — a non-ASCII BMP filler keeps the character count exact while still exercising
        // the two-bytes-per-character payload maths.
        string longPiece = new('ż', length);
        Fragment fragment = new() { Pieces = [longPiece] };

        // Act
        Fragment parsed = RoundTrip(fragment);

        // Assert
        parsed.Pieces.ShouldBe([longPiece]);
    }

    [Theory]
    [InlineData(VarLenSingleByteCeiling)]
    [InlineData(VarLenTwoByteCeiling)]
    public void IsWritablePiece_PieceUpToTheVarLenCeiling_ShouldBeTrue(int length)
    {
        // Arrange
        string piece = new('ż', length);

        // Act
        bool writable = Fragment.IsWritablePiece(piece);

        // Assert
        writable.ShouldBeTrue();
    }

    [Fact]
    public void IsWritablePiece_PieceLongerThanTheVarLenCeiling_ShouldBeFalse()
    {
        // Arrange — the screen PatchingService runs before it mutates a loaded subfile (#598), so an
        // over-long row costs one warning instead of a mid-loop throw over an already-written DAT.
        string piece = new('ż', VarLenTwoByteCeiling + 1);

        // Act
        bool writable = Fragment.IsWritablePiece(piece);

        // Assert
        writable.ShouldBeFalse();
    }

    [Fact]
    public void Write_PieceLongerThanTheVarLenCeiling_ShouldThrow()
    {
        // Arrange — the deliberate last resort behind IsWritablePiece, not a reachable failure mode:
        // VarLen cannot express a length above 32767 and a truncated prefix would silently corrupt
        // every following fragment, so writing one anyway is a programmer error. Callers screen the
        // content first (#598, ADR-0043); this pins that Write itself never degrades to truncation.
        Fragment fragment = new() { Pieces = [new string('ż', VarLenTwoByteCeiling + 1)] };
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.Unicode, leaveOpen: true);

        // Act
        Action write = () => fragment.Write(writer);

        // Assert
        write.ShouldThrow<ArgumentOutOfRangeException>();
    }

    private static Fragment RoundTrip(Fragment fragment)
    {
        using MemoryStream stream = new();

        using (BinaryWriter writer = new(stream, Encoding.Unicode, leaveOpen: true))
        {
            fragment.Write(writer);
        }

        stream.Position = 0;
        using BinaryReader reader = new(stream, Encoding.Unicode, leaveOpen: true);

        Fragment parsed = new();
        parsed.Parse(reader);

        return parsed;
    }
}
