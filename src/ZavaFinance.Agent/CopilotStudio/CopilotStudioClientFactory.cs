// Copyright (c) Microsoft Corporation.

using Microsoft.Agents.CopilotStudio.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ZavaFinance.Core.Abstractions;

namespace ZavaFinance.Core.CopilotStudio;

/// <summary>
/// Builds a <see cref="CopilotClient"/> for one call. A client is never cached or shared: the
/// token it carries belongs to a single user and a single turn.
/// </summary>
public interface ICopilotStudioClientFactory
{
    CopilotClient Create(IDownstreamTokenProvider tokenProvider);
}

/// <summary>
/// <para>
/// <see cref="CopilotClient"/> accepts an arbitrary <c>Func&lt;string, Task&lt;string&gt;&gt;</c>
/// as its token source, so it carries no dependency on Bot Framework or on a turn context. That
/// is what lets the same subagent client serve the Teams / Microsoft 365 Copilot channel and the
/// Foundry hosted agent, changing only which <see cref="IDownstreamTokenProvider"/> is supplied.
/// </para>
/// </summary>
public sealed class CopilotStudioClientFactory : ICopilotStudioClientFactory
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CopilotStudioClientFactory> _logger;
    private readonly string _httpClientName;

    public CopilotStudioClientFactory(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<CopilotStudioClientFactory> logger,
        string httpClientName = "copilotstudio")
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _httpClientName = httpClientName;
    }

    public CopilotClient Create(IDownstreamTokenProvider tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);

        var settings = new ConnectionSettings(_configuration.GetSection("CopilotStudioAgent"));

        // Never hand-write this scope. It is derived from EnvironmentId / SchemaName / Cloud,
        // and a hand-built URI fails with an opaque 401.
        string[] scopes = [CopilotClient.ScopeFromSettings(settings)];

        return new CopilotClient(
            settings,
            _httpClientFactory,
            tokenProviderFunction: async _ =>
                await tokenProvider.GetTokenAsync(scopes, CancellationToken.None),
            _logger,
            _httpClientName);
    }
}
