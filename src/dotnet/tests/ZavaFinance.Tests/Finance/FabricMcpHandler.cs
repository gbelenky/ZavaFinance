using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ZavaFinance.Tests.Finance;

internal sealed class FabricMcpHandler : HttpMessageHandler
{
    internal const string CapacityLimitBody =
        """
        {"error":{"code":-32002,"message":"Your organization's Fabric compute capacity has exceeded its limits. Try again later.","data":{"errorCode":"CapacityLimitExceeded"}},"jsonrpc":"2.0","id":null}
        """;

    internal ConcurrentQueue<Request> Requests { get; } = new();
    internal bool Sse { get; init; }
    internal bool SupplySession { get; init; } = true;
    internal string Answer { get; init; } = "recovered";
    internal string? CallResult { get; init; }
    internal string ToolsResult { get; init; } =
        """
        {"tools":[{"name":"DataAgent_Zava","inputSchema":{"type":"object","properties":{"userQuestion":{"type":"string"}}}}]}
        """;
    internal Func<Request, CancellationToken, Task<HttpResponseMessage?>>? BeforeReply { get; init; }

    internal sealed record Request(
        string Method, string? Uri, JsonElement? Message, string? Authorization,
        string? Session, string? Protocol, string[] Accept);

    internal static HttpResponseMessage Reply(Request request, string result, bool sse = false, bool error = false)
    {
        string body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request.Message!.Value.GetProperty("id"),
            [error ? "error" : "result"] = JsonSerializer.Deserialize<JsonElement>(result)
        });
        if (sse)
        {
            // Comments and multiple data lines exercise the SDK's SSE parser, not a local parser.
            body = ": keep-alive\r\n\r\nevent: message\r\ndata: {\r\ndata: " + body[1..] + "\r\n\r\n";
        }

        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, sse ? "text/event-stream" : "application/json")
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        JsonElement? message = request.Content is null ? null
            : JsonSerializer.Deserialize<JsonElement>(await request.Content.ReadAsStringAsync(cancellationToken));
        var captured = new Request(
            message?.GetProperty("method").GetString() ?? request.Method.Method,
            request.RequestUri?.ToString(), message, request.Headers.Authorization?.ToString(),
            Header("Mcp-Session-Id"), Header("MCP-Protocol-Version"),
            request.Headers.Accept.Select(value => value.MediaType!).ToArray());
        Requests.Enqueue(captured);

        if (BeforeReply is not null && await BeforeReply(captured, cancellationToken) is { } overridden)
        {
            return overridden;
        }

        switch (captured.Method)
        {
            case "initialize":
                HttpResponseMessage initialized = Reply(captured,
                    """
                    {"protocolVersion":"2025-06-18","capabilities":{"tools":{}},"serverInfo":{"name":"Fake Fabric","version":"1.0"}}
                    """, Sse);
                if (SupplySession)
                {
                    initialized.Headers.Add("Mcp-Session-Id", $"session-{request.Headers.Authorization?.Parameter}");
                }

                return initialized;
            case "tools/list":
                return Reply(captured, ToolsResult, Sse);
            case "tools/call":
                return Reply(captured, CallResult ?? JsonSerializer.Serialize(
                    new { content = new[] { new { type = "text", text = Answer } } }), Sse);
            case "notifications/initialized":
            case "notifications/cancelled":
                return new(HttpStatusCode.Accepted);
            case "DELETE":
                return new(HttpStatusCode.NoContent);
            default:
                throw new InvalidOperationException($"Unexpected MCP request: {captured.Method}");
        }

        string? Header(string name) =>
            request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.Single() : null;
    }
}
