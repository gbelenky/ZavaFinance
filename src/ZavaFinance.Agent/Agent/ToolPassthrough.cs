// Copyright (c) Microsoft Corporation.

namespace ZavaFinance.Core.Agent;

/// <summary>
/// Carries a tool's answer around the model instead of through it.
/// <para>
/// An orchestrator is judged on routing, not prose. When a tool returns authoritative content —
/// a sourced Copilot Studio answer, or a statement full of figures — re-emitting it through
/// the model is both wasteful and lossy: a measured 2,827-character KPIpedia answer came back
/// as 1,119 characters with its SharePoint citation stripped, despite instructions to return
/// it verbatim. Prompt wording cannot guarantee byte-for-byte fidelity, so the tool result is
/// captured here and delivered directly.
/// </para>
/// <para>
/// One instance per turn. It is never shared between callers.
/// </para>
/// </summary>
public sealed class ToolPassthrough
{
    /// <summary>The last tool answer of this turn, or <see langword="null"/> if no tool ran.</summary>
    public string? Answer { get; private set; }

    /// <summary>True when a tool produced the answer, so the model's text is not used.</summary>
    public bool HasAnswer => Answer is not null;

    public void Capture(string answer) => Answer = answer;
}
