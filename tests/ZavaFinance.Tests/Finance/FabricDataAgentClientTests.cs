// Copyright (c) Microsoft Corporation.

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ZavaFinance.Core.Configuration;
using ZavaFinance.Core.Finance;
using Xunit;

namespace ZavaFinance.Tests.Finance;

/// <summary>
/// Response parsing for the Fabric data agent MCP endpoint.
/// <para>
/// The endpoint is streamable HTTP, so the same logical reply can arrive as a plain JSON-RPC
/// body or wrapped in a single SSE frame. Handling only one of those shapes produced a working
/// integration that failed intermittently depending on how long the answer took.
/// </para>
/// </summary>
public sealed class FabricDataAgentClientTests
{
    [Fact]
    public void ParsesPlainJsonRpcBody()
    {
        const string Body =
            """{"result":{"content":[{"type":"text","text":"hello"}],"isError":false},"id":3,"jsonrpc":"2.0"}""";

        JsonElement message = FabricDataAgentClient.ParseMessage(Body);

        Assert.Equal(
            "hello",
            message.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void ParsesServerSentEventFrame()
    {
        const string Body =
            "event: message\ndata: {\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]},\"id\":3}\n\n";

        JsonElement message = FabricDataAgentClient.ParseMessage(Body);

        Assert.Equal(
            "hi",
            message.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void ParsesSseFrameWithLeadingWhitespace()
    {
        const string Body =
            "\n\nevent: message\ndata: {\"result\":{\"content\":[]},\"id\":1}\n";

        JsonElement message = FabricDataAgentClient.ParseMessage(Body);

        Assert.True(message.TryGetProperty("result", out _));
    }

    [Fact]
    public void SurfacesJsonRpcErrors()
    {
        const string Body =
            """{"error":{"code":-32000,"message":"agent not published"},"id":1,"jsonrpc":"2.0"}""";

        JsonElement message = FabricDataAgentClient.ParseMessage(Body);

        Assert.True(message.TryGetProperty("error", out JsonElement error));
        Assert.Equal("agent not published", error.GetProperty("message").GetString());
    }

    /// <summary>
    /// The Preview runtime returns an occasional 500 on long multi-step questions, and the
    /// identical question then succeeds. Surfacing that as a failed answer would show the user a
    /// dead end for a question the agent can actually answer.
    /// </summary>
    [Fact]
    public async Task RetriesOnceWhenTheToolCallReturnsServerError()
    {
        var handler = new StubHandler(
            [
                Ok(InitializeBody),
                Ok(ToolListBody),
                new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(
                        """{"error":{"code":-32603,"message":"internal error"},"id":3}""")
                },
                Ok(AnswerBody)
            ]);

        string answer = await CreateClient(handler).AskAsync("why?", CancellationToken.None);

        Assert.Equal("recovered", answer);
        Assert.Equal(4, handler.Calls);
    }

    /// <summary>
    /// One retry, not a loop. A second failure would push the turn past its budget, and the
    /// user is better served by a graceful message than by a longer silence.
    /// </summary>
    [Fact]
    public async Task DoesNotRetryTwice()
    {
        var handler = new StubHandler(
            [
                Ok(InitializeBody),
                Ok(ToolListBody),
                new HttpResponseMessage(HttpStatusCode.InternalServerError),
                new HttpResponseMessage(HttpStatusCode.InternalServerError)
            ]);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateClient(handler).AskAsync("why?", CancellationToken.None));

        Assert.Equal(4, handler.Calls);
    }

    /// <summary>
    /// A 4xx is the agent telling us the request is wrong — unpublished, unauthorised, or a bad
    /// argument. Retrying it just doubles the latency before the same failure.
    /// </summary>
    [Fact]
    public async Task DoesNotRetryClientErrors()
    {
        var handler = new StubHandler(
            [
                Ok(InitializeBody),
                Ok(ToolListBody),
                new HttpResponseMessage(HttpStatusCode.Forbidden)
            ]);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateClient(handler).AskAsync("why?", CancellationToken.None));

        Assert.Equal(3, handler.Calls);
    }

    /// <summary>
    /// A 429 from Fabric means the capacity is throttled, not that the agent is unreachable, and
    /// it is not retried: capacity throttling outlasts a turn, so an immediate second attempt
    /// would spend the user's latency budget to fail identically.
    /// </summary>
    [Fact]
    public async Task DoesNotRetryCapacityThrottling()
    {
        var handler = new StubHandler(
            [
                Ok(InitializeBody),
                Ok(ToolListBody),
                new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent(CapacityLimitBody)
                }
            ]);

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateClient(handler).AskAsync("why?", CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(3, handler.Calls);
    }

    internal const string CapacityLimitBody =
        """
        {"error":{"code":-32002,"message":"Your organization's Fabric compute capacity has exceeded its limits. Try again later.","data":{"errorCode":"CapacityLimitExceeded"}},"jsonrpc":"2.0","id":null}
        """;

    private const string InitializeBody =
        """{"result":{"protocolVersion":"2025-06-18"},"id":1,"jsonrpc":"2.0"}""";

    private const string ToolListBody =
        """
        {"result":{"tools":[{"name":"DataAgent_Zava","inputSchema":{"properties":{"userQuestion":{"type":"string"}}}}]},"id":2,"jsonrpc":"2.0"}
        """;

    private const string AnswerBody =
        """{"result":{"content":[{"type":"text","text":"recovered"}]},"id":3,"jsonrpc":"2.0"}""";

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static FabricDataAgentClient CreateClient(StubHandler handler)
        => new(
            new HttpClient(handler),
            new FabricOptions { WorkspaceId = "ws", DataAgentId = "agent" },
            _ => Task.FromResult("token"),
            NullLogger.Instance);

    private sealed class StubHandler(HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = responses[Calls];
            Calls++;
            return Task.FromResult(response);
        }
    }
}
