// Copyright (c) Microsoft Corporation.

using Microsoft.Extensions.Logging;
using ZavaFinance.Core.Abstractions;
using ZavaFinance.Core.Configuration;

namespace ZavaFinance.Core.Finance;

/// <summary>Builds a statement query bound to one caller.</summary>
public interface IStatementQueryFactory
{
    IStatementQuery Create(IDownstreamTokenProvider tokenProvider);
}

/// <summary>Builds a data agent client bound to one caller.</summary>
public interface IFabricDataAgentClientFactory
{
    FabricDataAgentClient Create(IDownstreamTokenProvider tokenProvider);
}

/// <summary>
/// Creates Fabric clients for one caller.
/// <para>
/// Nothing here is cached or shared: the token is per user and per call. One exchangeable user
/// token is exchanged once per downstream audience — Copilot Studio, the SQL analytics endpoint,
/// and the Fabric REST API — so a single sign-in serves all three.
/// </para>
/// </summary>
public sealed class FabricClientFactory : IStatementQueryFactory, IFabricDataAgentClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FabricOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public FabricClientFactory(
        IHttpClientFactory httpClientFactory,
        FabricOptions options,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _loggerFactory = loggerFactory;
    }

    public IStatementQuery Create(IDownstreamTokenProvider tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);

        if (!_options.IsSqlConfigured)
        {
            // Keeps local development and unconfigured environments answering rather than
            // failing the turn with a connection error the user cannot act on.
            return new UnavailableStatementQuery();
        }

        return new FabricStatementQuery(
            _options,
            ct => tokenProvider.GetTokenAsync(FabricOptions.SqlScopes, ct),
            _loggerFactory.CreateLogger<FabricStatementQuery>());
    }

    FabricDataAgentClient IFabricDataAgentClientFactory.Create(
        IDownstreamTokenProvider tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);

        return new FabricDataAgentClient(
            _httpClientFactory.CreateClient(nameof(FabricDataAgentClient)),
            _options,
            ct => tokenProvider.GetTokenAsync(FabricOptions.DataAgentScopes, ct),
            _loggerFactory.CreateLogger<FabricDataAgentClient>());
    }
}

/// <summary>
/// Stand-in used when the lakehouse is not configured. It reports the gap instead of throwing,
/// because every tool needs a graceful failure path.
/// </summary>
internal sealed class UnavailableStatementQuery : IStatementQuery
{
    public Task<OrganizationScope?> ResolveOrganizationAsync(
        string org, CancellationToken cancellationToken) =>
        Task.FromResult<OrganizationScope?>(new OrganizationScope("unconfigured", org, org));

    public Task<StatementResult> GetStatementAsync(
        KpiDefinition kpi,
        OrganizationScope organization,
        FinancePeriod period,
        CancellationToken cancellationToken) =>
        Task.FromResult(new StatementResult(kpi, organization, period, null, null, "USD"));
}
