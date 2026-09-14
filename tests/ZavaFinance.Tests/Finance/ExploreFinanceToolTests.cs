// Copyright (c) Microsoft Corporation.

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Configuration;
using ZavaFinance.Core.Finance;
using ZavaFinance.Core.Tools;
using Xunit;

namespace ZavaFinance.Tests.Finance;

/// <summary>
/// Failure paths for <see cref="ExploreFinanceTool"/>.
/// <para>
/// Every tool needs a graceful failure path, but a graceful message that misdescribes the fault
/// is its own defect: it sends the user, and whoever they escalate to, looking for the wrong
/// problem. These tests pin the distinction that matters operationally — the agent was
/// unreachable, versus the agent was reached and refused.
/// </para>
/// </summary>
public sealed class ExploreFinanceToolTests
{
    /// <summary>
    /// Fabric answers 429 CapacityLimitExceeded when the capacity backing the workspace is
    /// throttled. Observed in production on an F2 capacity, and reported to the user as
    /// "I could not reach the finance data agent" — which is wrong twice over: the request was
    /// delivered, and the fix is to wait or scale rather than to investigate connectivity.
    /// </summary>
    [Fact]
    public async Task CapacityThrottlingIsReportedAsBusyRatherThanUnreachable()
    {
        string answer = await RunWithAsync(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(FabricDataAgentClientTests.CapacityLimitBody)
            });

        Assert.Contains("busy", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("capacity", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not reach", answer, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A genuine transport failure keeps the original message, so the two causes stay
    /// distinguishable from the user's side.
    /// </summary>
    [Fact]
    public async Task OtherFailuresStillReportUnreachable()
    {
        string answer = await RunWithAsync(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        Assert.Contains("could not reach", answer, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The tool never throws: an unhandled exception inside the durable continuation is silence
    /// in Teams, which is the one outcome a user cannot act on.
    /// </summary>
    [Fact]
    public async Task NeverThrows()
    {
        string answer = await RunWithAsync(
            new HttpResponseMessage(HttpStatusCode.Forbidden));

        Assert.False(string.IsNullOrWhiteSpace(answer));
    }

    private static async Task<string> RunWithAsync(HttpResponseMessage failure)
    {
        // The initialize and tools/list calls succeed so the failure lands on the tools/call
        // step, which is where a real capacity rejection occurs.
        var handler = new SequenceHandler(
            [
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"result":{"protocolVersion":"2025-06-18"},"id":1,"jsonrpc":"2.0"}""")
                },
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"result":{"tools":[{"name":"DataAgent","inputSchema":{"properties":{"userQuestion":{"type":"string"}}}}]},"id":2,"jsonrpc":"2.0"}""")
                },
                failure,
                failure
            ]);

        var options = new FabricOptions
        {
            WorkspaceId = "ws",
            DataAgentId = "agent"
        };

        var client = new FabricDataAgentClient(
            new HttpClient(handler),
            options,
            _ => Task.FromResult("token"),
            NullLogger.Instance);

        var passthrough = new ToolPassthrough();

        var tool = new ExploreFinanceTool(
            client,
            new OrchestratorSessionState(),
            passthrough,
            options,
            NullLogger.Instance);

        return await tool.ExploreFinanceAsync("why did margin move?", CancellationToken.None);
    }

    private sealed class SequenceHandler(HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response =
                responses[Math.Min(_calls, responses.Length - 1)];

            _calls++;
            return Task.FromResult(response);
        }
    }
}
