// Copyright (c) Microsoft Corporation.

namespace ZavaFinance.Agent;

/// <summary>
/// Identifies the build that is actually running.
/// <para>
/// The hosted-agent deploy is content-addressed: an unchanged package is skipped and the previous
/// version keeps serving. That is usually what you want, but it makes a stale deployment
/// indistinguishable from an unfixed defect — the same exception keeps appearing at the same
/// place, and the only clue is that its stack-trace line numbers no longer match the source.
/// Logging this marker at startup makes the question answerable in one query.
/// </para>
/// </summary>
internal static class ThisAssembly
{
    /// <summary>
    /// Bump when a deploy must be forced. The value participates in the package content, so
    /// changing it guarantees a new agent version.
    /// </summary>
    public const string BuildMarker = "2026-09-14.9-source-attribution";
}
