// Copyright (c) Microsoft Corporation.

namespace ZavaFinance.Core.Tools;

/// <summary>
/// The one-line attribution appended to every tool answer.
/// <para>
/// Each tool answers from a different system — the lakehouse, the Fabric data agent, or the
/// Copilot Studio knowledge base — and the three read identically once they reach the user. Naming
/// the system is the difference between a figure the user can check and a number that merely
/// appeared, which matters most for the two natural-language tools, whose text is generated
/// elsewhere and passed through untouched.
/// </para>
/// <para>
/// The footer is appended <b>after</b> the answer, never woven into it. The subagent answers are
/// returned verbatim on purpose — a paraphrased figure is a wrong figure, and a dropped citation is
/// an unsourced claim — so attribution has to be additive or it would defeat the passthrough.
/// </para>
/// </summary>
public static class SourceFooter
{
    /// <summary>Structured figures, computed here from lakehouse components.</summary>
    public const string Lakehouse = "Zava finance lakehouse";

    /// <summary>Open-ended analysis, answered by the published Fabric data agent.</summary>
    public const string DataAgent = "Zava finance data agent (Microsoft Fabric)";

    /// <summary>KPI definitions, answered by the Copilot Studio knowledge base.</summary>
    public const string KnowledgeBase = "KPIpedia (Copilot Studio)";

    private const string Marker = "_Source:";

    /// <summary>
    /// Returns <paramref name="answer"/> with an attribution line appended.
    /// </summary>
    /// <remarks>
    /// Returns the answer unchanged when it is empty, or when it already carries an attribution
    /// line — a tool that composes its own source text must not end up with two.
    /// </remarks>
    public static string Append(string answer, string source)
    {
        if (string.IsNullOrWhiteSpace(answer) || answer.Contains(Marker, StringComparison.Ordinal))
        {
            return answer;
        }

        // A blank line, so the footer renders as its own paragraph rather than joining the last
        // sentence of the answer.
        return $"{answer.TrimEnd()}\n\n{Marker} {source}._";
    }
}
