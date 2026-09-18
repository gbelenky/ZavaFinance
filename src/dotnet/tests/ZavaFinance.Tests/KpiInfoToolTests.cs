using Microsoft.Agents.CopilotStudio.Client;
using Microsoft.Extensions.Logging.Abstractions;
using ZavaFinance.Core.Abstractions;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Configuration;
using ZavaFinance.Core.CopilotStudio;
using ZavaFinance.Core.Tools;
using Xunit;

namespace ZavaFinance.Tests;

public sealed class KpiInfoToolTests
{
    [Fact]
    public async Task ClarificationReturnsDirectlyWithoutCreatingAClient()
    {
        var factory = new CancelledClientFactory(CancellationToken.None);
        var tool = new KpiInfoTool(factory, new UnusedTokens(), new OrchestratorSessionState(),
            new OrchestratorOptions(), NullLogger.Instance);

        Assert.Equal("I need a KPI name to look up.", await tool.GetKpiInfoAsync("", CancellationToken.None));
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToAnAnswer()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var state = new OrchestratorSessionState();
        var tool = new KpiInfoTool(new CancelledClientFactory(cts.Token), new UnusedTokens(), state,
            new OrchestratorOptions(), NullLogger.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.GetKpiInfoAsync("EBIT", cts.Token));
        Assert.Null(state.LastKpiName);
    }

    [Fact]
    public async Task DownstreamTimeoutStillReturnsAnExplicitMessage()
    {
        var tool = new KpiInfoTool(new CancelledClientFactory(CancellationToken.None),
            new UnusedTokens(), new OrchestratorSessionState(), new OrchestratorOptions(), NullLogger.Instance);
        Assert.Equal("KPIpedia did not respond in time for 'EBIT'. Please try again.",
            await tool.GetKpiInfoAsync("EBIT", CancellationToken.None));
    }

    private sealed class CancelledClientFactory(CancellationToken cancellationToken) : ICopilotStudioClientFactory
    {
        public int Calls { get; private set; }
        public CopilotClient Create(IDownstreamTokenProvider tokenProvider)
        {
            Calls++;
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class UnusedTokens : IDownstreamTokenProvider
    {
        public Task<string> GetTokenAsync(string[] scopes, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No token acquisition is expected.");
    }
}
