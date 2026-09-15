using Microsoft.Extensions.Logging.Abstractions;
using ZavaFinance.Core.Abstractions;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Configuration;
using ZavaFinance.Core.Finance;
using ZavaFinance.Core.Tools;
using Xunit;

namespace ZavaFinance.Tests.Finance;

public sealed class StatementToolTests
{
    [Fact]
    public async Task UnconfiguredWarehouseIsNotMisreportedAsMissingData()
    {
        var factory = new FabricClientFactory(new UnusedHttpFactory(), new FabricOptions(), NullLoggerFactory.Instance);
        IStatementQuery? query = factory.Create(new UnusedTokens());
        Assert.Null(query);
        var state = new OrchestratorSessionState { LastKpiName = "EBIT" };
        var tool = new StatementTool(query, state, new DateOnly(2026, 9, 15), NullLogger.Instance);

        string answer = await tool.GetStatementAsync("Net Revenue", "EMEA", "Q2 2026");

        Assert.Equal("The finance warehouse is not configured in this environment.", answer);
        Assert.DoesNotContain("No data", answer);
        Assert.Equal("EBIT", state.LastKpiName);
    }

    [Fact]
    public async Task ClarificationStillPrecedesWarehouseAccess()
    {
        var tool = new StatementTool(null, new OrchestratorSessionState(), new DateOnly(2026, 9, 15),
            NullLogger.Instance);
        Assert.Equal("Which KPI would you like a statement for?", await tool.GetStatementAsync());
        Assert.StartsWith("Which organization", await tool.GetStatementAsync("Net Revenue"));
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToAFinanceAnswer()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var tool = new StatementTool(new CancelledQuery(), new OrchestratorSessionState(),
            new DateOnly(2026, 9, 15), NullLogger.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tool.GetStatementAsync("Net Revenue", "EMEA", "Q2 2026", cts.Token));
    }

    private sealed class UnusedHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Must not create HTTP client.");
    }

    private sealed class UnusedTokens : IDownstreamTokenProvider
    {
        public Task<string> GetTokenAsync(string[] scopes, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Must not acquire tokens.");
    }

    private sealed class CancelledQuery : IStatementQuery
    {
        public Task<OrganizationScope?> ResolveOrganizationAsync(string org, CancellationToken cancellationToken) =>
            Task.FromCanceled<OrganizationScope?>(cancellationToken);
        public Task<StatementResult> GetStatementAsync(
            KpiDefinition kpi, OrganizationScope organization, FinancePeriod period, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Must not run query.");
    }
}
