using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using DiffEngine;
using VerifyTests;

namespace LotroKoniecDev.Tests.Shared;

/// <summary>
/// The Verify configuration for the whole repo (#571). The file is linked into every suite that takes a
/// snapshot, so all of them scrub values the same way, like the shared naughty-string sources (#569,
/// <see cref="NaughtyStringCases"/>).
/// </summary>
/// <remarks>
/// <para>
/// A snapshot that changes on every run is worse than no snapshot, so every value that differs between
/// runs, machines or environments is scrubbed here.
/// Each scrubber matches a shape on purpose and replaces as little as possible: a pattern only fires on
/// text that still looks like a GUID, a timestamp or a digest, and everything that carries meaning stays
/// in the snapshot. So a change that alters the shape leaves the raw value in place and fails the test,
/// which is the whole point of pinning the payload.
/// </para>
/// <para>
/// A note on order: the scrubbers run over the same text, so a new one must not eat what an earlier one
/// is meant to see. Today they cannot overlap. A W3C traceparent is 32 or 16 hex characters and
/// <c>HttpContext.TraceIdentifier</c> looks like <c>0Hxxx:0000001</c>, so neither has the shape of a
/// GUID or of a 64-character hex digest, and <see cref="TraceIdPattern"/> is anchored on the property
/// name anyway.
/// </para>
/// <para>
/// Verify resolves a verified file from the test's <c>[CallerFilePath]</c>. That means enabling
/// <c>ContinuousIntegrationBuild</c> (which turns on <c>DeterministicSourcePaths</c> and rewrites
/// caller paths to <c>/_/…</c>) would break every snapshot suite at once with a misleading
/// "file not found". Nothing in the repo sets it; if that ever changes, pin the location explicitly
/// with <c>VerifierSettings.DerivePathInfo</c> off the MSBuild project directory instead.
/// </para>
/// </remarks>
internal static partial class VerifyModuleInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Ids: identical values collapse to the same Guid_N marker, so the snapshot still proves that
        // an item's id and the id inside its HATEOAS href are the same entity.
        VerifierSettings.ScrubInlineGuids();

        VerifierSettings.AddScrubber(builder => ReplaceAll(builder, TimestampPattern(), "{Timestamp}"));
        VerifierSettings.AddScrubber(builder => ReplaceAll(builder, HexDigestPattern(), "{HexDigest}"));
        VerifierSettings.AddScrubber(builder => ReplaceAll(builder, TraceIdPattern(), "$1{TraceId}$2"));

        // Never launch a diff tool: most runs here are headless (CI, the backlog loop), and a GUI
        // popping up mid-run would hang the session rather than report a failure.
        DiffRunner.Disabled = true;
    }

    private static void ReplaceAll(StringBuilder builder, Regex pattern, string replacement)
    {
        string scrubbed = pattern.Replace(builder.ToString(), replacement);
        builder.Clear();
        builder.Append(scrubbed);
    }

    /// <summary>
    /// The date-and-time half of an ISO-8601 instant as <c>System.Text.Json</c> writes
    /// <c>DateTimeOffset</c> / <c>DateTime</c>. The trailing designator is matched by lookahead and
    /// therefore <em>survives</em> into the snapshot: an instant that starts being serialized in
    /// server-local time reads as <c>{Timestamp}+02:00</c> instead of <c>{Timestamp}Z</c> and fails
    /// the test, which scrubbing the offset away would have hidden.
    /// </summary>
    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(?=Z|[+-]\d{2}:\d{2})")]
    private static partial Regex TimestampPattern();

    /// <summary>
    /// Content digests: the translation file's SHA-256 in hex, which is also served as the strong
    /// <c>ETag</c> (AUDIT-SEC-01, #391), plus any other 64-character hex digest. It matches whole words,
    /// so a longer run of hex stays visible instead of being half replaced.
    /// It is here just in case: no snapshot carries a digest today, so this does not cover the ETag.
    /// </summary>
    [GeneratedRegex(@"\b[0-9a-fA-F]{64}\b")]
    private static partial Regex HexDigestPattern();

    /// <summary>
    /// The <c>traceId</c> ASP.NET Core stamps on every <c>ProblemDetails</c> body. Matched by property
    /// name rather than by shape on purpose: the value is a W3C traceparent when an <c>Activity</c> is
    /// running and the raw <c>HttpContext.TraceIdentifier</c> when one is not, so which shape a run
    /// produces depends on whether tracing is wired up in that environment.
    /// </summary>
    [GeneratedRegex(@"(""traceId""\s*:\s*"")[^""]*("")")]
    private static partial Regex TraceIdPattern();
}
