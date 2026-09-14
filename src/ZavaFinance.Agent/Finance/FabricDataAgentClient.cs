// Copyright (c) Microsoft Corporation.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZavaFinance.Core.Configuration;

namespace ZavaFinance.Core.Finance;

/// <summary>
/// Client for a published Fabric data agent, over its Model Context Protocol endpoint.
/// <para>
/// Every call is made with a **delegated user token**, so the agent answers under the caller's
/// permissions.
/// </para>
/// <para>
/// This deliberately does not use the OpenAI Assistants surface
/// (<c>/aiassistant/openai/threads</c>). OpenAI sunset the Assistants API on 26 August 2026 and
/// Fabric's documentation now directs integrations to the MCP endpoint. The old path still
/// accepts requests — it creates threads and runs quite happily — but every run then fails with
/// <c>invalid_prompt: BadRequest</c> from inside the service, which reads like a broken agent
/// rather than a removed API. The Fabric portal kept working throughout because it had already
/// moved.
/// </para>
/// </summary>
public sealed class FabricDataAgentClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string ProtocolVersion = "2025-06-18";

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
    /// MCP calls are self-contained, so unlike the Copilot Studio subagent there is no
    /// conversation handle to store. That removes a whole class of isolation risk: there is no
    /// shared thread two participants could ever resume.
    /// </para>
    /// </summary>
    public async Task<string> AskAsync(string question, CancellationToken cancellationToken)
    {
        string token = await _tokenProvider(cancellationToken);

        await SendAsync(
            token,
            new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { },
                    clientInfo = new { name = "ZavaFinance", version = "1.0" }
                }
            },
            cancellationToken);

        (string toolName, string argumentName) = await DiscoverToolAsync(token, cancellationToken);

        // The argument name comes from the tool's own input schema rather than being hard-coded,
        // because it is the agent's contract and can differ per agent.
        JsonElement call = await SendAsync(
            token,
            new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new
                {
                    name = toolName,
                    arguments = new Dictionary<string, string> { [argumentName] = question }
                }
            },
            retryOnServerError: true,
            cancellationToken);

        return ReadAnswer(call);
    }

    private async Task<(string ToolName, string ArgumentName)> DiscoverToolAsync(
        string token, CancellationToken cancellationToken)
    {
        JsonElement list = await SendAsync(
            token,
            new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } },
            cancellationToken);

        if (!list.TryGetProperty("result", out JsonElement result)
            || !result.TryGetProperty("tools", out JsonElement tools)
            || tools.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Fabric data agent exposes no MCP tool.");
        }

        JsonElement tool = tools[0];

        string name = tool.GetProperty("name").GetString()
            ?? throw new InvalidOperationException("Fabric data agent tool has no name.");

        string argument = "userQuestion";

        if (tool.TryGetProperty("inputSchema", out JsonElement schema)
            && schema.TryGetProperty("properties", out JsonElement properties))
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                argument = property.Name;
                break;
            }
        }

        return (name, argument);
    }

    private async Task<JsonElement> SendAsync(
        string token, object payload, CancellationToken cancellationToken)
        => await SendAsync(token, payload, retryOnServerError: false, cancellationToken);

    private async Task<JsonElement> SendAsync(
        string token, object payload, bool retryOnServerError, CancellationToken cancellationToken)
    {
        int attempt = 0;

        while (true)
        {
            attempt++;

            try
            {
                return await SendOnceAsync(token, payload, cancellationToken);
            }
            catch (HttpRequestException ex)
                when (retryOnServerError
                      && attempt == 1
                      && ex.StatusCode is >= HttpStatusCode.InternalServerError
                      && !cancellationToken.IsCancellationRequested)
            {
                // The Preview runtime returns an occasional 500 on long multi-step questions;
                // the identical question then succeeds. Retrying is safe because an MCP call
                // carries no conversation state, so a replay cannot duplicate a turn the way a
                // Copilot Studio call would.
                _logger.LogWarning(
                    ex,
                    "Fabric data agent returned {Status}; retrying once.",
                    (int)ex.StatusCode.Value);
            }
        }
    }

    private async Task<JsonElement> SendOnceAsync(
        string token, object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new("Bearer", token);

        // The endpoint is streamable HTTP: it may answer with JSON or with an SSE frame, and
        // advertising only JSON is rejected.
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Content = JsonContent.Create(payload, options: Json);

        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, cancellationToken);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Fabric data agent MCP call failed with {Status}. {Detail}",
                (int)response.StatusCode,
                body);

            response.EnsureSuccessStatusCode();
        }

        JsonElement message = ParseMessage(body);

        if (message.TryGetProperty("error", out JsonElement error))
        {
            string detail = error.TryGetProperty("message", out JsonElement m)
                ? m.GetString() ?? "unknown"
                : "unknown";

            throw new InvalidOperationException($"Fabric data agent returned an error: {detail}");
        }

        return message;
    }

    /// <summary>
    /// Reads either a plain JSON-RPC body or a single SSE frame, since the endpoint may use
    /// either depending on how the answer is produced.
    /// </summary>
    internal static JsonElement ParseMessage(string body)
    {
        string payload = body.TrimStart();

        if (!payload.StartsWith('{'))
        {
            foreach (string line in body.Split('\n'))
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    payload = line["data:".Length..].Trim();
                    break;
                }
            }
        }

        return JsonDocument.Parse(payload).RootElement.Clone();
    }

    private string ReadAnswer(JsonElement message)
    {
        if (!message.TryGetProperty("result", out JsonElement result))
        {
            return string.Empty;
        }

        if (result.TryGetProperty("isError", out JsonElement isError)
            && isError.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException("Fabric data agent reported a tool error.");
        }

        if (!result.TryGetProperty("content", out JsonElement content))
        {
            return string.Empty;
        }

        var parts = new List<string>();

        foreach (JsonElement part in content.EnumerateArray())
        {
            if (part.TryGetProperty("type", out JsonElement type)
                && type.GetString() == "text"
                && part.TryGetProperty("text", out JsonElement text))
            {
                parts.Add(text.GetString() ?? string.Empty);
            }
        }

        string answer = string.Join("\n", parts).Trim();

        // Shape only, never content: the answer is permissioned user data.
        _logger.LogInformation(
            "Fabric data agent replied. Parts={Parts} Length={Length}", parts.Count, answer.Length);

        return answer;
    }
}
