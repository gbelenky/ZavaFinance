// Copyright (c) Microsoft Corporation.

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task EarlierHttpCancellationIsNotReportedAsTheWholeTurnBudget()
    {
        using var handler = new FabricMcpHandler
        {
            BeforeReply = async (request, token) =>
            {
                if (request.Method == "tools/call")
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                return null;
            }
        };
        var logger = new CaptureLogger();

        string answer = await RunWithAsync(
            handler, httpTimeout: TimeSpan.FromSeconds(1), logger: logger)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("did not respond in time", answer);
        Assert.Single(handler.Requests, request => request.Method == "tools/call");
        Assert.DoesNotContain(logger.Messages, message => message.Contains("timed out after"));
        Assert.Contains(logger.Messages, message => message.Contains("before the"));
    }

    [Theory]
    [InlineData("token", false)]
    [InlineData("initialize", false)]
    [InlineData("tools/list", false)]
    [InlineData("tools/call", false)]
    [InlineData("response-json", false)]
    [InlineData("response-sse", false)]
    [InlineData("token", true)]
    [InlineData("initialize", true)]
    [InlineData("tools/list", true)]
    [InlineData("tools/call", true)]
    [InlineData("response-json", true)]
    [InlineData("response-sse", true)]
    public async Task AnalysisCancellationCoversDelegationAndEveryMcpStage(
        string stage, bool callerCancellation)
    {
        using var caller = new CancellationTokenSource();
        CancellationToken observedToken = default;
        PendingResponseStream? responseStream = null;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task BlockAsync(CancellationToken token)
        {
            observedToken = token;
            if (callerCancellation)
            {
                caller.Cancel();
            }
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                stopped.TrySetResult();
            }
        }

        using var handler = new FabricMcpHandler
        {
            BeforeReply = async (request, token) =>
            {
                if (request.Method == stage)
                {
                    await BlockAsync(token);
                }
                if (request.Method == "tools/call" && stage.StartsWith("response-", StringComparison.Ordinal))
                {
                    responseStream = new PendingResponseStream(BlockAsync);
                    var content = new StreamContent(responseStream);
                    content.Headers.ContentType = new(
                        stage == "response-sse" ? "text/event-stream" : "application/json");
                    return new(HttpStatusCode.OK) { Content = content };
                }
                return null;
            }
        };
        var options = new FabricOptions
        {
            WorkspaceId = "ws",
            DataAgentId = "agent",
            DataAgentTimeout = callerCancellation ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(3)
        };
        var logger = new CaptureLogger();

        Task<string> execution = RunWithAsync(
            handler, options: options, cancellationToken: caller.Token,
            httpTimeout: TimeSpan.FromSeconds(10), logger: logger, tokenProvider: async token =>
            {
                if (stage == "token")
                {
                    await BlockAsync(token);
                }
                return "token";
            }).WaitAsync(TimeSpan.FromSeconds(10));
        if (callerCancellation)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            Assert.DoesNotContain(logger.Messages, message => message.Contains("explore_finance"));
        }
        else
        {
            Assert.Contains("did not respond in time", await execution);
            Assert.Contains(logger.Messages, message => message.Contains("timed out after"));
        }

        try
        {
            Assert.True(observedToken.CanBeCanceled,
                "The configured budget must allow the request to reach the selected test stage.");
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            responseStream?.Dispose();
        }
        Assert.True(observedToken.IsCancellationRequested);
        if (stage == "token")
        {
            Assert.Empty(handler.Requests);
        }
        else
        {
            string requestMethod = stage.StartsWith("response-", StringComparison.Ordinal) ? "tools/call" : stage;
            Assert.Single(handler.Requests, request => request.Method == requestMethod);
        }
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
        CancellationToken cancellationToken = default,
        TimeSpan? httpTimeout = null,
        ILogger? logger = null,
        Func<CancellationToken, Task<string>>? tokenProvider = null)
    {
        options ??= new FabricOptions { WorkspaceId = "ws", DataAgentId = "agent" };
        using var http = new HttpClient(handler, disposeHandler: false);
        if (httpTimeout is { } timeout)
        {
            http.Timeout = timeout;
        }
        var client = new FabricDataAgentClient(
            http, options, tokenProvider ?? (_ => Task.FromResult("token")), NullLogger.Instance);
        var tool = new ExploreFinanceTool(client, options, logger ?? NullLogger.Instance);

        return await tool.ExploreFinanceAsync(question, cancellationToken);
    }

    private sealed class PendingResponseStream(Func<CancellationToken, Task> read) : Stream
    {
        private readonly CancellationTokenSource _closed = new();
        private bool _disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            await read(pending.Token);
            return 0;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _closed.Cancel();
                _closed.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
