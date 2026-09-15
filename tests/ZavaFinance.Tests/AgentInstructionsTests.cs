// Copyright (c) Microsoft Corporation.

using ZavaFinance.Core.Agent;
using Xunit;

namespace ZavaFinance.Tests;

public sealed class AgentInstructionsTests
{
    /// <summary>
    /// The prompt must keep the model in the role of a router.
    /// <para>
    /// The model selects a route and extracts arguments; the host runs the tool and returns its
    /// output verbatim. If the prompt ever stopped saying so, the model would start answering
    /// finance questions itself from whatever it remembers, which is the failure this whole
    /// design exists to prevent.
    /// </para>
    /// </summary>
    [Fact]
    public void PromptRoutesWithoutExecutingTools()
    {
        string prompt = OrchestratorAgent.SystemInstructions;

        Assert.Contains(FinanceToolNames.KpiInfoTool, prompt, StringComparison.Ordinal);
        Assert.Contains(FinanceToolNames.StatementTool, prompt, StringComparison.Ordinal);
        Assert.Contains(FinanceToolNames.ExploreFinanceTool, prompt, StringComparison.Ordinal);
        Assert.Contains("native function calling", prompt, StringComparison.OrdinalIgnoreCase);

        // The host executes the tool, not the model, and the result is returned unaltered.
        Assert.Contains("host executes", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verbatim", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never answer", prompt, StringComparison.OrdinalIgnoreCase);
    }
}
