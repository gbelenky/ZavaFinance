// Copyright (c) Microsoft Corporation.

using Xunit;
using ZavaFinance.Core.Tools;

namespace ZavaFinance.Tests;

/// <summary>
/// Every tool names the system its answer came from.
/// <para>
/// This used to be true of <c>get_statement</c> only, because that answer is composed here while
/// the other two are passed through verbatim from a subagent. The asymmetry was not deliberate:
/// the two natural-language tools are precisely the ones whose text is generated elsewhere, so
/// they are the ones where naming the source matters most.
/// </para>
/// </summary>
public sealed class SourceFooterTests
{
    [Fact]
    public void AppendsAttributionAfterTheAnswer()
    {
        string result = SourceFooter.Append("The answer.", SourceFooter.DataAgent);

        Assert.StartsWith("The answer.", result, StringComparison.Ordinal);
        Assert.Contains("_Source: Zava finance data agent (Microsoft Fabric)._", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// The subagent answers are returned verbatim on purpose, so attribution must be additive.
    /// A paraphrased figure is a wrong figure and a dropped citation is an unsourced claim.
    /// </summary>
    [Fact]
    public void LeavesTheAnswerBodyUntouched()
    {
        const string Answer = "Net Revenue rose 19.2% [1]\n\n| Region | Value |\n|---|---|";

        string result = SourceFooter.Append(Answer, SourceFooter.KnowledgeBase);

        Assert.StartsWith(Answer, result, StringComparison.Ordinal);
    }

    /// <summary>A tool that composes its own source text must not end up with two.</summary>
    [Fact]
    public void DoesNotAppendTwice()
    {
        string once = SourceFooter.Append("The answer.", SourceFooter.Lakehouse);
        string twice = SourceFooter.Append(once, SourceFooter.DataAgent);

        Assert.Equal(once, twice);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void LeavesEmptyAnswersAlone(string answer)
        => Assert.Equal(answer, SourceFooter.Append(answer, SourceFooter.Lakehouse));

    /// <summary>
    /// The three sources are distinct strings, so a reader can tell which system answered.
    /// </summary>
    [Fact]
    public void EachToolNamesADifferentSystem()
    {
        string[] sources =
        [
            SourceFooter.Lakehouse,
            SourceFooter.DataAgent,
            SourceFooter.KnowledgeBase
        ];

        Assert.Equal(sources.Length, sources.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
