// Copyright (c) Microsoft Corporation.

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ZavaFinance.Core.Configuration;
using ZavaFinance.Core.Finance;
using ZavaFinance.Core.Tools;
using Xunit;

namespace ZavaFinance.Tests.Finance;

public sealed class ExploreFinanceToolTests
{
    [Fact]
    public async Task CapacityThrottlingIsReportedAsBusyRatherThanUnreachable()
    {
        using var handler = FailToolCall(HttpStatusCode.TooManyRequests);

        string answer = await RunWithAsync(handler);

        Assert.Contains("busy", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("capacity", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not reach", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests, request => request.Method == "tools/call");
        AssertValidHandshake(handler);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, 2)]
    [InlineData(HttpStatusCode.Forbidden, 1)]
    [InlineData(HttpStatusCode.Unauthorized, 1)]
    public async Task OtherFailuresStillReportUnreachable(HttpStatusCode status, int attempts)
    {
        using var handler = FailToolCall(status);

        string answer = await RunWithAsync(handler);

        Assert.Equal("I could not reach the finance data agent for that analysis.", answer);
        Assert.Equal(attempts, handler.Requests.Count(request => request.Method == "tools/call"));
        AssertValidHandshake(handler);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtocolFailuresReturnAGracefulMessageRatherThanErrorContent(bool sse)
    {
        using var handler = new FabricMcpHandler
        {
            Sse = sse,
            BeforeReply = (request, _) => Task.FromResult<HttpResponseMessage?>(
                request.Method == "tools/call"
                    ? FabricMcpHandler.Reply(request,
                        """{"code":-32000,"message":"internal service detail"}""", sse, error: true)
                    : null)
        };

        string answer = await RunWithAsync(handler);

        Assert.Equal("I could not reach the finance data agent for that analysis.", answer);
        Assert.DoesNotContain("internal service detail", answer);
        Assert.Single(handler.Requests, request => request.Method == "tools/call");
        AssertValidHandshake(handler);
    }

    [Fact]
    public async Task ToolErrorsReturnAGracefulMessageRatherThanErrorContent()
    {
        using var handler = new FabricMcpHandler
        {
            CallResult = """{"isError":true,"content":[{"type":"text","text":"internal service detail"}]}"""
        };

        string answer = await RunWithAsync(handler);

        Assert.Equal("I could not reach the finance data agent for that analysis.", answer);
        Assert.Single(handler.Requests, request => request.Method == "tools/call");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturnsTheDownstreamAnswerWithAttributionWithoutParaphrasing(bool sse)
    {
        const string Answer = "  Margin fell by 2.4 pp.\r\n\r\n| Driver | Impact |\n| Cost | -€12 |";
        using var handler = new FabricMcpHandler { Sse = sse, Answer = Answer };

        string answer = await RunWithAsync(handler);

        Assert.Equal($"{Answer}\n\n_Source: Zava finance data agent (Microsoft Fabric)._", answer);
        Assert.Single(handler.Requests, request => request.Method == "tools/call");
        AssertValidHandshake(handler);
    }

    [Fact]
    public async Task AlreadyAttributedAnswerIsReturnedVerbatim()
    {
        const string Answer = "  Analysis.\r\n_Source: Published report._\r\n  ";
        using var handler = new FabricMcpHandler { Answer = Answer };

        Assert.Equal(Answer, await RunWithAsync(handler));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public async Task EmptyAnswersReturnAnExplicitFallback(string answer)
    {
        using var handler = new FabricMcpHandler { Answer = answer };

        Assert.Equal("The finance data agent did not return an answer for that question.",
            await RunWithAsync(handler));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public async Task BlankQuestionsDoNotContactFabric(string question)
    {
        using var handler = new FabricMcpHandler();

        Assert.Equal("What would you like me to analyse?", await RunWithAsync(handler, question: question));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MissingConfigurationDoesNotContactFabric()
    {
        using var handler = new FabricMcpHandler();

        Assert.Equal("Open-ended finance analysis is not configured in this environment.",
            await RunWithAsync(handler, options: new FabricOptions()));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CallerCancellationPropagatesRatherThanBecomingAGracefulFailure()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new FabricMcpHandler
        {
            BeforeReply = async (request, token) =>
            {
                if (request.Method == "tools/call")
                {
                    cancellation.Cancel();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                return null;
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunWithAsync(handler, cancellationToken: cancellation.Token));

        Assert.Single(handler.Requests, request => request.Method == "tools/call");
    }

    [Fact]
    public async Task ExpiredTurnBudgetReturnsATimeoutMessageWithoutRetrying()
    {
        using var handler = new FabricMcpHandler
        {
            BeforeReply = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            }
        };
        var options = new FabricOptions
        {
            WorkspaceId = "ws",
            DataAgentId = "agent",
            DataAgentTimeout = TimeSpan.FromMilliseconds(100)
        };

        string answer = await RunWithAsync(handler, options: options);

        Assert.Contains("did not respond in time", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(handler.Requests, request => request.Method == "tools/call");
    }

    private static FabricMcpHandler FailToolCall(HttpStatusCode status) => new()
    {
        BeforeReply = (request, _) => Task.FromResult<HttpResponseMessage?>(
            request.Method == "tools/call"
                ? new(status)
                {
                    Content = new StringContent(FabricMcpHandler.CapacityLimitBody, Encoding.UTF8, "application/json")
                }
                : null)
    };

    private static void AssertValidHandshake(FabricMcpHandler handler)
    {
        Assert.Equal(["initialize", "notifications/initialized", "tools/list"],
            handler.Requests.Take(3).Select(request => request.Method));
        Assert.All(handler.Requests.Where(request => request.Method != "initialize"),
            request => Assert.Equal("session-token", request.Session));
    }

    private static async Task<string> RunWithAsync(
        FabricMcpHandler handler,
        string question = "why did margin move?",
        FabricOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new FabricOptions { WorkspaceId = "ws", DataAgentId = "agent" };
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new FabricDataAgentClient(
            http, options, _ => Task.FromResult("token"), NullLogger.Instance);
        var tool = new ExploreFinanceTool(client, options, NullLogger.Instance);

        return await tool.ExploreFinanceAsync(question, cancellationToken);
    }
}
