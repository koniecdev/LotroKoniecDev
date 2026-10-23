using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using DiffEngine;
using VerifyTests;

namespace LotroKoniecDev.Tests.Shared;

/// <summary>
/// The Verify setup for every snapshot suite (#571). The file is linked into each one, so all of them
/// scrub values the same way.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot that changes on every run is worse than no snapshot, so every value that differs between
/// runs, machines or environments is scrubbed. Each scrubber fires only on text that still has its
/// shape (a GUID, a timestamp, a digest), so a change that alters the shape leaves the raw value in
/// place and fails the test.
/// </para>
/// <para>
/// The scrubbers run over the same text, so a new one must not eat what an earlier one is meant to see.
/// Today they cannot overlap: a W3C traceparent (32 or 16 hex characters) and
/// <c>HttpContext.TraceIdentifier</c> (<c>0Hxxx:0000001</c>) have neither the GUID nor the 64-character
/// digest shape, and <see cref="TraceIdPattern"/> is anchored on the property name anyway.
/// </para>
/// <para>
/// Verify finds a verified file from the test's <c>[CallerFilePath]</c>. Turning on
/// <c>ContinuousIntegrationBuild</c> rewrites caller paths to <c>/_/…</c> and would break every
/// snapshot suite at once with a misleading "file not found". Nothing sets it today; if that changes,
/// pin the location with <c>VerifierSettings.DerivePathInfo</c> off the project directory.
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

        // Most runs are headless (CI, the backlog loop), and a diff tool popping up would hang the run
        // instead of reporting a failure.
        DiffRunner.Disabled = true;
    }

    private static void ReplaceAll(StringBuilder builder, Regex pattern, string replacement)
    {
        string scrubbed = pattern.Replace(builder.ToString(), replacement);
        builder.Clear();
        builder.Append(scrubbed);
    }

    /// <summary>
    /// The date and time of an ISO-8601 instant. The zone designator is matched by lookahead, so it
    /// stays in the snapshot: an instant that starts coming out in server-local time reads
    /// <c>{Timestamp}+02:00</c> instead of <c>{Timestamp}Z</c> and fails the test.
    /// </summary>
    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(?=Z|[+-]\d{2}:\d{2})")]
    private static partial Regex TimestampPattern();

    /// <summary>
    /// A 64-character hex digest, such as the translation file's SHA-256 that is also its strong
    /// <c>ETag</c> (#391). Whole words only, so a longer hex run stays visible instead of being half
    /// replaced. No snapshot carries a digest today; this guards the first one that does.
    /// </summary>
    [GeneratedRegex(@"\b[0-9a-fA-F]{64}\b")]
    private static partial Regex HexDigestPattern();

    /// <summary>
    /// The <c>traceId</c> on every <c>ProblemDetails</c> body, matched by property name, not by shape:
    /// it is a W3C traceparent when an <c>Activity</c> is running and the raw
    /// <c>HttpContext.TraceIdentifier</c> when not, so its shape depends on whether tracing is wired up.
    /// </summary>
    [GeneratedRegex(@"(""traceId""\s*:\s*"")[^""]*("")")]
    private static partial Regex TraceIdPattern();
}
