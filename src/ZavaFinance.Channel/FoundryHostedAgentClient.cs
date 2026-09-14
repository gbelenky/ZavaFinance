// Copyright (c) Microsoft Corporation.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace ZavaFinance.Channel;

/// <summary>Calls the Foundry hosted agent for one user turn.</summary>
public interface IHostedAgentClient
{
    /// <summary>
    /// Creates a platform conversation, whose id is reused for every later turn of the same
    /// user session.
    /// </summary>
    Task<string> CreateConversationAsync(CancellationToken cancellationToken);

    Task<string> AskAsync(
        string question,
        string userAssertion,
        string? conversationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Invokes the hosted agent's Responses endpoint.
/// <para>
/// Two different tokens travel on this one request, and conflating them is the mistake to avoid:
/// </para>
/// <list type="table">
/// <item>
/// <term><c>Authorization</c></term>
/// <description>
/// The channel's own managed-identity token for <c>https://ai.azure.com/.default</c>. It
/// authenticates this workload to Foundry, and says nothing about which user is asking.
/// </description>
/// </item>
/// <item>
/// <term><c>x-client-user-token</c></term>
/// <description>
/// The signed-in user's assertion, forwarded unchanged. Foundry neither authenticates nor
/// interprets it; it forwards <c>x-client-*</c> headers to the container, where the agent
/// validates it and exchanges it On-Behalf-Of.
/// </description>
/// </item>
/// </list>
/// <para>
/// A separate header is required because one bearer token cannot carry two audiences, and because
/// the gateway deliberately does not forward the caller's own <c>Authorization</c> header.
/// </para>
/// <para>
/// The assertion is forwarded and then dropped. It is never logged, never persisted, and never
/// written into an orchestration payload.
/// </para>
/// </summary>
public sealed class FoundryHostedAgentClient : IHostedAgentClient
{
    private static readonly string[] FoundryScopes = ["https://ai.azure.com/.default"];

    private readonly HttpClient _httpClient;
    private readonly TokenCredential _credential;
    private readonly HostedAgentOptions _options;
    private readonly ILogger<FoundryHostedAgentClient> _logger;

    public FoundryHostedAgentClient(
        HttpClient httpClient,
        TokenCredential credential,
        HostedAgentOptions options,
        ILogger<FoundryHostedAgentClient> logger)
    {
        _httpClient = httpClient;
        _credential = credential;
        _options = options;
        _logger = logger;
    }

    public async Task<string> CreateConversationAsync(CancellationToken cancellationToken)
    {
        AccessToken workloadToken = await _credential.GetTokenAsync(
            new TokenRequestContext(FoundryScopes), cancellationToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, _options.ConversationsEndpoint);

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", workloadToken.Token);
        request.Content = JsonContent.Create(new { });

        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, cancellationToken);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Could not create a hosted agent conversation. Status={Status} Detail={Detail}",
                (int)response.StatusCode,
                body);

            response.EnsureSuccessStatusCode();
        }

        using JsonDocument document = JsonDocument.Parse(body);

        return document.RootElement.TryGetProperty("id", out JsonElement id)
            ? id.GetString() ?? string.Empty
            : string.Empty;
    }

    public async Task<string> AskAsync(
        string question,
        string userAssertion,
        string? conversationId,
        CancellationToken cancellationToken)
    {
        AccessToken workloadToken = await _credential.GetTokenAsync(
            new TokenRequestContext(FoundryScopes), cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.ResponsesEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", workloadToken.Token);
        request.Headers.Add(_options.UserTokenHeader, userAssertion);

        // conversation is omitted rather than sent empty: the endpoint validates the identifier
        // format and rejects anything that is not a platform conv_ id.
        object payload = string.IsNullOrWhiteSpace(conversationId)
            ? new
            {
                model = _options.AgentName,
                input = question,
                stream = false,
                store = false
            }
            : new
            {
                model = _options.AgentName,
                input = question,
                conversation = new { id = conversationId },
                stream = false,

                // The agent owns per-user state under a key it derives from the assertion, so
                // the platform does not need to retain this turn on our behalf.
                store = false
            };

        request.Content = JsonContent.Create(payload);

        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, cancellationToken);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // A 4xx here is a contract problem — a malformed identifier, a wrong audience — and
            // the body names it. The body of a failed call carries no answer text, so logging it
            // does not expose the user's data, and without it the only symptom is a status code.
            _logger.LogWarning(
                "Hosted agent call failed. Status={Status} Detail={Detail}",
                (int)response.StatusCode,
                body);

            response.EnsureSuccessStatusCode();
        }

        return ExtractOutputText(body);
    }

    /// <summary>
    /// Reads the assistant text out of a Responses payload.
    /// <para>
    /// <c>output_text</c> is read in preference to reconstructing from content parts, and the
    /// result is returned whole. The tool's answer already passed through the agent verbatim, so
    /// any reshaping here would undo that: a paraphrased figure is a wrong figure, and a dropped
    /// citation is an unsourced claim.
    /// </para>
    /// </summary>
    internal static string ExtractOutputText(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("output_text", out JsonElement direct)
            && direct.ValueKind == JsonValueKind.String)
        {
            return direct.GetString() ?? string.Empty;
        }

        if (!root.TryGetProperty("output", out JsonElement output)
            || output.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var text = new System.Text.StringBuilder();

        foreach (JsonElement item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out JsonElement content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out JsonElement value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    text.Append(value.GetString());
                }
            }
        }

        return text.ToString();
    }
}

/// <summary>Configuration for reaching the hosted agent.</summary>
public sealed class HostedAgentOptions
{
    public const string SectionName = "HostedAgent";

    /// <summary>Full URL of the agent's Responses endpoint in the Foundry project.</summary>
    public string ResponsesEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Conversations endpoint, derived from the Responses endpoint by default. The Responses
    /// endpoint rejects any conversation id it did not issue, so conversations are created here.
    /// </summary>
    public string ConversationsEndpoint
    {
        get => string.IsNullOrWhiteSpace(_conversationsEndpoint)
            ? ResponsesEndpoint.Replace("/responses", "/conversations", StringComparison.Ordinal)
            : _conversationsEndpoint;
        set => _conversationsEndpoint = value;
    }

    private string? _conversationsEndpoint;

    /// <summary>The deployed agent name, sent as the model identifier.</summary>
    public string AgentName { get; set; } = "zavafinance";

    /// <summary>
    /// Must match the header the hosted agent reads. Foundry forwards only <c>x-client-*</c>
    /// headers to the container.
    /// </summary>
    public string UserTokenHeader { get; set; } = "x-client-user-token";

    /// <summary>
    /// Ceiling for one hosted-agent call. The slow tools are natural-language endpoints: the
    /// Copilot Studio subagent measures around 51 seconds and the Fabric data agent has been
    /// observed between 25 seconds and over three minutes for the same question.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResponsesEndpoint);
}
