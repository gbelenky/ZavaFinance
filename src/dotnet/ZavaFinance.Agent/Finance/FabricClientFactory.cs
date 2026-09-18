// Copyright (c) Microsoft Corporation.

using Microsoft.Extensions.Logging;
using ZavaFinance.Core.Abstractions;
using ZavaFinance.Core.Configuration;

namespace ZavaFinance.Core.Finance;

/// <summary>Builds a statement query bound to one caller.</summary>
public interface IStatementQueryFactory
{
    /// <returns>A caller-bound query, or null when SQL is not configured.</returns>
    IStatementQuery? Create(IDownstreamTokenProvider tokenProvider);
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

    public IStatementQuery? Create(IDownstreamTokenProvider tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);

        if (!_options.IsSqlConfigured)
        {
            return null;
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
