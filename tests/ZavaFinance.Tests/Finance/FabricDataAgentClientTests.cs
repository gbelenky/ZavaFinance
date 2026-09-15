// Copyright (c) Microsoft Corporation.

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ZavaFinance.Core.Configuration;
using ZavaFinance.Core.Finance;
using Xunit;
using static ZavaFinance.Tests.Finance.FabricMcpHandler;

namespace ZavaFinance.Tests.Finance;

public sealed class FabricDataAgentClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsesMcpHandshakeDiscoveryAndInvocationWithVerbatimText(bool sse)
    {
        const string Question = "Why did revenue change?\r\nKeep the quoted \"region\".";
        const string Answer = "  Revenue changed.\r\n\r\n| Region | Δ |\r\n| East | €42 |\n  ";
        using var handler = new FabricMcpHandler { Sse = sse, Answer = Answer };
        using var http = new HttpClient(handler);

        string answer = await CreateClient(http).AskAsync(Question, CancellationToken.None);

        Assert.Equal(Answer, answer);
        Request[] requests = handler.Requests.ToArray();
        Assert.Equal(
            ["initialize", "notifications/initialized", "tools/list", "tools/call", "DELETE"],
            requests.Select(request => request.Method));
        Assert.Equal("2025-06-18",
            requests[0].Message!.Value.GetProperty("params").GetProperty("protocolVersion").GetString());
        Assert.Equal("ZavaFinance",
            requests[0].Message!.Value.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.False(requests[1].Message!.Value.TryGetProperty("id", out _));
        JsonElement call = requests[3].Message!.Value.GetProperty("params");
        Assert.Equal("DataAgent_Zava", call.GetProperty("name").GetString());
        Assert.Equal(Question, call.GetProperty("arguments").GetProperty("userQuestion").GetString());
        Assert.Single(call.GetProperty("arguments").EnumerateObject());
        Assert.Null(requests[0].Session);
        Assert.All(requests.Skip(1), request =>
        {
            Assert.Equal("session-token", request.Session);
            Assert.Equal("2025-06-18", request.Protocol);
        });
        Assert.All(requests, request =>
        {
            Assert.Equal("Bearer token", request.Authorization);
            Assert.Equal("https://api.fabric.microsoft.com/v1/mcp/workspaces/ws/dataagents/agent/agent", request.Uri);
        });
        Assert.All(requests.Where(request => request.Method != "DELETE"), request =>
        {
            Assert.Contains("application/json", request.Accept);
            Assert.Contains("text/event-stream", request.Accept);
        });
    }

    [Fact]
    public async Task UsesFirstPublishedToolAndItsFirstSchemaProperty()
    {
        using var handler = new FabricMcpHandler
        {
            ToolsResult = """
                {"tools":[
                  {"name":"PublishedFinance","inputSchema":{"type":"object","properties":{"question":{"type":"string"},"optional":{"type":"string"}}}},
                  {"name":"OtherTool","inputSchema":{"type":"object","properties":{"input":{"type":"string"}}}}
                ]}
                """
        };
        using var http = new HttpClient(handler);

        await CreateClient(http).AskAsync("exact question", CancellationToken.None);

        JsonElement call = Assert.Single(handler.Requests, r => r.Method == "tools/call")
            .Message!.Value.GetProperty("params");
        Assert.Equal("PublishedFinance", call.GetProperty("name").GetString());
        Assert.Equal("exact question", call.GetProperty("arguments").GetProperty("question").GetString());
        Assert.Single(call.GetProperty("arguments").EnumerateObject());
    }

    [Fact]
    public async Task FallsBackToUserQuestionWhenSchemaHasNoProperties()
    {
        using var handler = new FabricMcpHandler
        {
            ToolsResult = """{"tools":[{"name":"DataAgent_Zava","inputSchema":{"type":"object"}}]}"""
        };
        using var http = new HttpClient(handler);

        await CreateClient(http).AskAsync("question", CancellationToken.None);

        JsonElement arguments = Assert.Single(handler.Requests, r => r.Method == "tools/call")
            .Message!.Value.GetProperty("params").GetProperty("arguments");
        Assert.Equal("question", arguments.GetProperty("userQuestion").GetString());
    }

    [Fact]
    public async Task RejectsAnEmptyToolListWithoutCallingATool()
    {
        using var handler = new FabricMcpHandler { ToolsResult = """{"tools":[]}""" };
        using var http = new HttpClient(handler);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateClient(http).AskAsync("question", CancellationToken.None));

        Assert.Equal("Fabric data agent exposes no MCP tool.", error.Message);
        Assert.DoesNotContain(handler.Requests, request => request.Method == "tools/call");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JoinsOnlyTextBlocksWithoutTrimmingTheirContent(bool sse)
    {
        using var handler = new FabricMcpHandler
        {
            Sse = sse,
            CallResult = """
                {"content":[
                  {"type":"text","text":"  first\r\nline\n"},
                  {"type":"image","data":"AQID","mimeType":"image/png"},
                  {"type":"text","text":"\nsecond  "}
                ],"structuredContent":{"text":"must not replace the answer"}}
                """
        };
        using var http = new HttpClient(handler);

        string answer = await CreateClient(http).AskAsync("question", CancellationToken.None);

        Assert.Equal("  first\r\nline\n\n\nsecond  ", answer);
    }

    [Theory]
    [InlineData("""{"content":[]}""")]
    [InlineData("""{"content":[{"type":"image","data":"AQID","mimeType":"image/png"}]}""")]
    public async Task ReturnsEmptyWhenThereIsNoText(string result)
    {
        using var handler = new FabricMcpHandler { CallResult = result };
        using var http = new HttpClient(handler);

        Assert.Equal(string.Empty, await CreateClient(http).AskAsync("question", CancellationToken.None));
    }

    [Fact]
    public async Task WorksWithoutAServerSessionAndDoesNotDisposeTheSuppliedHttpClient()
    {
        using var handler = new FabricMcpHandler { SupplySession = false };
        using var http = new HttpClient(handler);
        FabricDataAgentClient client = CreateClient(http);

        await client.AskAsync("first", CancellationToken.None);
        await client.AskAsync("second", CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count(r => r.Method == "initialize"));
        Assert.All(handler.Requests, request => Assert.Null(request.Session));
        Assert.DoesNotContain(handler.Requests, request => request.Method == "DELETE");
    }

    [Fact]
    public async Task ConcurrentCallersKeepDelegatedTokensAndSessionsIsolatedOnASharedHttpClient()
    {
        var bothCallersInitialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int initialized = 0;
        using var handler = new FabricMcpHandler
        {
            BeforeReply = async (request, token) =>
            {
                if (request.Method == "initialize")
                {
                    if (Interlocked.Increment(ref initialized) == 2)
                    {
                        bothCallersInitialized.SetResult();
                    }

                    await bothCallersInitialized.Task.WaitAsync(token);
                }

                return null;
            }
        };
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("X-Shared-Client", "unchanged");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Task.WhenAll(
            CreateClient(http, _ => Task.FromResult("alice")).AskAsync("alice question", timeout.Token),
            CreateClient(http, _ => Task.FromResult("bob")).AskAsync("bob question", timeout.Token));

        Assert.Null(http.DefaultRequestHeaders.Authorization);
        Assert.False(http.DefaultRequestHeaders.Contains("Mcp-Session-Id"));
        Assert.Equal("unchanged", Assert.Single(http.DefaultRequestHeaders.GetValues("X-Shared-Client")));
        foreach (string caller in new[] { "alice", "bob" })
        {
            Request[] requests = handler.Requests.Where(r => r.Authorization == $"Bearer {caller}").ToArray();
            Assert.Equal(5, requests.Length);
            Assert.Null(requests[0].Session);
            Assert.All(requests.Skip(1), r => Assert.Equal($"session-{caller}", r.Session));
            Assert.Equal($"{caller} question", Assert.Single(requests, r => r.Method == "tools/call")
                .Message!.Value.GetProperty("params").GetProperty("arguments").GetProperty("userQuestion").GetString());
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SessionCleanupFailureDoesNotDiscardACompletedAnswer(HttpStatusCode status)
    {
        using var handler = new FabricMcpHandler
        {
            Answer = "Completed finance answer.",
            BeforeReply = (request, _) => Task.FromResult<HttpResponseMessage?>(
                request.Method == "DELETE" ? new(status) : null)
        };
        using var http = new HttpClient(handler);

        Assert.Equal("Completed finance answer.",
            await CreateClient(http).AskAsync("question", CancellationToken.None));
        Assert.Single(handler.Requests, request => request.Method == "tools/call");
        Assert.Single(handler.Requests, request => request.Method == "DELETE");
    }

    [Fact]
    public async Task ObtainsAFreshDelegatedTokenForEachQuestion()
    {
        using var handler = new FabricMcpHandler();
        using var http = new HttpClient(handler);
        int tokenRequests = 0;
        FabricDataAgentClient client = CreateClient(http, _ => Task.FromResult($"token-{++tokenRequests}"));

        await client.AskAsync("first", CancellationToken.None);
        await client.AskAsync("second", CancellationToken.None);

        Assert.Equal(2, tokenRequests);
        Assert.Equal(["Bearer token-1", "Bearer token-2"],
            handler.Requests.Where(r => r.Method == "tools/call").Select(r => r.Authorization));
        Assert.Equal(["session-token-1", "session-token-2"],
            handler.Requests.Where(r => r.Method == "tools/call").Select(r => r.Session));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task RetriesAToolServerErrorOnceWithTheSameArgumentsAndSession(HttpStatusCode status)
    {
        int calls = 0;
        using var handler = new FabricMcpHandler
        {
            BeforeReply = (request, _) => Task.FromResult<HttpResponseMessage?>(
                request.Method == "tools/call" && ++calls == 1 ? new(status) : null)
        };
        using var http = new HttpClient(handler);

        Assert.Equal("recovered", await CreateClient(http).AskAsync("why?", CancellationToken.None));

        Request[] callsMade = handler.Requests.Where(r => r.Method == "tools/call").ToArray();
        Assert.Equal(2, callsMade.Length);
        Assert.Equal(callsMade[0].Message!.Value.GetProperty("params").GetRawText(),
            callsMade[1].Message!.Value.GetProperty("params").GetRawText());
        Assert.Equal(callsMade[0].Session, callsMade[1].Session);
        Assert.Single(handler.Requests, r => r.Method == "initialize");
        Assert.Single(handler.Requests, r => r.Method == "tools/list");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, 2)]
    [InlineData(HttpStatusCode.Unauthorized, 1)]
    [InlineData(HttpStatusCode.Forbidden, 1)]
    [InlineData(HttpStatusCode.NotFound, 1)]
    [InlineData(HttpStatusCode.TooManyRequests, 1)]
    public async Task PreservesHttpFailureStatusAndRetryLimit(HttpStatusCode status, int expectedCalls)
    {
        using var handler = new FabricMcpHandler
        {
            BeforeReply = (request, _) => Task.FromResult<HttpResponseMessage?>(
                request.Method == "tools/call"
                    ? new(status) { Content = new StringContent(CapacityLimitBody, Encoding.UTF8, "application/json") }
                    : null)
        };
        using var http = new HttpClient(handler);

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateClient(http).AskAsync("why?", CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(expectedCalls, handler.Requests.Count(r => r.Method == "tools/call"));
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("tools/list")]
    public async Task DoesNotRetrySetupServerErrors(string failedMethod)
    {
        using var handler = new FabricMcpHandler
        {
            BeforeReply = (request, _) => Task.FromResult<HttpResponseMessage?>(
                request.Method == failedMethod ? new(HttpStatusCode.InternalServerError) : null)
        };
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateClient(http).AskAsync("question", CancellationToken.None));

        Assert.Single(handler.Requests, r => r.Method == failedMethod);
        Assert.DoesNotContain(handler.Requests, r => r.Method == "tools/call");
    }

    [Theory]
    [InlineData("initialize", false)]
    [InlineData("tools/list", false)]
    [InlineData("tools/call", false)]
    [InlineData("tools/call", true)]
    public async Task SurfacesProtocolErrorsWithoutRetrying(string failedMethod, bool sse)
    {
        using var handler = new FabricMcpHandler
        {
            BeforeReply = (request, _) => Task.FromResult<HttpResponseMessage?>(
                request.Method == failedMethod
                    ? Reply(request, """{"code":-32000,"message":"agent not published"}""", sse, error: true)
                    : null)
        };
        using var http = new HttpClient(handler);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateClient(http).AskAsync("question", CancellationToken.None));

        Assert.StartsWith("Fabric data agent returned an error:", error.Message);
        Assert.Contains("agent not published", error.Message);
        Assert.Single(handler.Requests, r => r.Method == failedMethod);
    }

    [Fact]
    public async Task SurfacesToolErrorsWithoutRetryingOrReturningErrorContentAsAnAnswer()
    {
        using var handler = new FabricMcpHandler
        {
            CallResult = """{"isError":true,"content":[{"type":"text","text":"internal failure"}]}"""
        };
        using var http = new HttpClient(handler);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateClient(http).AskAsync("question", CancellationToken.None));

        Assert.Equal("Fabric data agent reported a tool error.", error.Message);
        Assert.Single(handler.Requests, r => r.Method == "tools/call");
    }

    [Fact]
    public async Task PassesCancellationToTheDelegatedTokenProviderBeforeAnyHttpRequest()
    {
        using var handler = new FabricMcpHandler();
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        FabricDataAgentClient client = CreateClient(http, token =>
        {
            Assert.Equal(cancellation.Token, token);
            token.ThrowIfCancellationRequested();
            return Task.FromResult("unused");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.AskAsync("question", cancellation.Token));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("tools/list")]
    [InlineData("tools/call")]
    public async Task CancelsInFlightProtocolRequestsWithoutRetrying(string cancelledMethod)
    {
        using var cancellation = new CancellationTokenSource();
        bool transportCancelled = false;
        using var handler = new FabricMcpHandler
        {
            BeforeReply = async (request, token) =>
            {
                if (request.Method == cancelledMethod)
                {
                    cancellation.Cancel();
                    transportCancelled = token.IsCancellationRequested;
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                return null;
            }
        };
        using var http = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateClient(http).AskAsync("question", cancellation.Token));

        Assert.True(transportCancelled);
        Assert.Single(handler.Requests, r => r.Method == cancelledMethod);
    }

    [Fact]
    public async Task DoesNotRetryAServerErrorAfterCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new FabricMcpHandler
        {
            BeforeReply = (request, _) =>
            {
                if (request.Method == "tools/call")
                {
                    cancellation.Cancel();
                    throw new HttpRequestException("cancelled server failure", null, HttpStatusCode.InternalServerError);
                }

                return Task.FromResult<HttpResponseMessage?>(null);
            }
        };
        using var http = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateClient(http).AskAsync("question", cancellation.Token));

        Assert.Single(handler.Requests, r => r.Method == "tools/call");
    }

    private static FabricDataAgentClient CreateClient(
        HttpClient http, Func<CancellationToken, Task<string>>? tokenProvider = null)
        => new(http, new FabricOptions { WorkspaceId = "ws", DataAgentId = "agent" },
            tokenProvider ?? (_ => Task.FromResult("token")), NullLogger.Instance);
}
