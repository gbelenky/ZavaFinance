// Copyright (c) Microsoft Corporation.

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ZavaFinance.Core.Configuration;

namespace ZavaFinance.Core.Finance;

/// <summary>
/// Client for a published Fabric data agent, over its Model Context Protocol endpoint.
/// <para>
/// Every call is made with a **delegated user token**, so the agent answers under the caller's
/// permissions.
/// </para>
/// </summary>
public sealed class FabricDataAgentClient
{
    private readonly HttpClient _httpClient;
    private readonly FabricOptions _options;
    private readonly Func<CancellationToken, Task<string>> _tokenProvider;
    private readonly ILogger _logger;

    public FabricDataAgentClient(
        HttpClient httpClient,
        FabricOptions options,
        Func<CancellationToken, Task<string>> tokenProvider,
        ILogger logger)
    {
        _httpClient = httpClient;
        _options = options;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    /// <summary>
    /// The MCP endpoint of the published agent. It resolves only once the agent is published;
    /// an unpublished agent returns an error even when the URL is correct.
    /// </summary>
    private string Endpoint =>
        $"https://api.fabric.microsoft.com/v1/mcp/workspaces/{_options.WorkspaceId}"
        + $"/dataagents/{_options.DataAgentId}/agent";

    /// <summary>
    /// Asks a question and returns the agent's answer.
    /// <para>
    /// Each question owns a fresh MCP session and delegated headers. Neither authentication nor
    /// session state is stored on the shared HTTP client or reused by another caller.
    /// </para>
    /// </summary>
    public async Task<string> AskAsync(string question, CancellationToken cancellationToken)
    {
        string token = await _tokenProvider(cancellationToken);

        await using var transport = new HttpClientTransport(new()
        {
            Endpoint = new Uri(Endpoint),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" }
        }, _httpClient);

        try
        {
            await using McpClient client = await McpClient.CreateAsync(transport, new()
            {
                ClientInfo = new() { Name = "ZavaFinance", Version = "1.0" },
                // Keep Fabric's handshake rather than probing newer, sessionless MCP revisions.
                ProtocolVersion = "2025-06-18"
            }, cancellationToken: cancellationToken);

            (string toolName, string argumentName) = await DiscoverToolAsync(client, cancellationToken);
            var arguments = new Dictionary<string, object?> { [argumentName] = question };

            CallToolResult result;
            try
            {
                result = await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);
            }
            catch (HttpRequestException ex)
                when (ex.StatusCode is >= HttpStatusCode.InternalServerError
                      && !cancellationToken.IsCancellationRequested)
            {
                // Preserve the single retry for Fabric's intermittent read-only query failures.
                _logger.LogWarning(
                    "Fabric data agent returned {Status}; retrying once.", (int)ex.StatusCode.Value);
                result = await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);
            }

            return ReadAnswer(result);
        }
        catch (McpException ex)
        {
            throw new InvalidOperationException($"Fabric data agent returned an error: {ex.Message}", ex);
        }
    }

    private static async Task<(string ToolName, string ArgumentName)> DiscoverToolAsync(
        McpClient client, CancellationToken cancellationToken)
    {
        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        if (tools.Count == 0)
        {
            throw new InvalidOperationException("Fabric data agent exposes no MCP tool.");
        }

        McpClientTool tool = tools[0];
        if (string.IsNullOrEmpty(tool.Name))
        {
            throw new InvalidOperationException("Fabric data agent tool has no name.");
        }
        string argument = "userQuestion";

        // Preserve the published agent's argument name, which can differ between agents.
        if (tool.JsonSchema.TryGetProperty("properties", out JsonElement properties))
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                argument = property.Name;
                break;
            }
        }

        return (tool.Name, argument);
    }

    private string ReadAnswer(CallToolResult result)
    {
        if (result.IsError == true)
        {
            throw new InvalidOperationException("Fabric data agent reported a tool error.");
        }

        string[] parts = result.Content.OfType<TextContentBlock>().Select(part => part.Text).ToArray();
        string answer = string.Join("\n", parts);

        // Shape only, never content: the answer is permissioned user data.
        _logger.LogInformation(
            "Fabric data agent replied. Parts={Parts} Length={Length}", parts.Length, answer.Length);

        return answer;
    }
}
