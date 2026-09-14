// Copyright (c) Microsoft Corporation.

using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Configuration;
using Xunit;

namespace ZavaFinance.Tests;

public sealed class OrchestratorOptionsTests
{
    [Fact]
    public void AcknowledgementText_IsToolNeutral()
    {
        var options = new OrchestratorOptions();

        Assert.Equal("Working on that…", options.AcknowledgementText);
        Assert.DoesNotContain("KPIpedia", options.AcknowledgementText);
    }

    /// <summary>
    /// Pins the user-visible cost of routing inside the agent rather than the channel.
    /// <para>
    /// The channel learns the route only once the answer is already in hand, so it cannot name the
    /// selected tool before running it. The acknowledgement is therefore tool-neutral, and the user
    /// sees it for the whole wait — tens of seconds to minutes for the two natural-language tools.
    /// </para>
    /// <para>
    /// This test exists so the limitation stays visible rather than becoming folklore. If a
    /// future change makes the route available early — by streaming, for example — the
    /// acknowledgement should become specific again, and this test should be the thing that has
    /// to be rewritten.
    /// </para>
    /// </summary>
    [Fact]
    public void AcknowledgementCannotNameTheToolInThisTopology()
    {
        var options = new OrchestratorOptions();

        foreach (string toolName in new[]
                 {
                     OrchestratorRoute.KpiInfoTool,
                     OrchestratorRoute.StatementTool,
                     OrchestratorRoute.ExploreFinanceTool
                 })
        {
            Assert.DoesNotContain(
                toolName,
                options.AcknowledgementText,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The wait is long enough that the acknowledgement is the only thing standing between the
    /// user and the conclusion that the agent is broken. It must never be empty.
    /// </summary>
    [Fact]
    public void AcknowledgementIsNeverSilent()
    {
        Assert.False(string.IsNullOrWhiteSpace(new OrchestratorOptions().AcknowledgementText));
    }
}
