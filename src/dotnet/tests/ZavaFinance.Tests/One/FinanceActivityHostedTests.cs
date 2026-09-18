using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.App.Proactive;
using Microsoft.Agents.Builder.App.UserAuth;
using Microsoft.Agents.Builder.UserAuth.TokenService;
using Microsoft.Agents.Connector;
using Microsoft.Agents.Core.HeaderPropagation;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Client;
using ZavaFinance.Agent;
using ZavaFinance.ActivitySupport;
using ZavaFinance.Contracts;
using ZavaFinance.Core.Abstractions;
using ZavaFinance.Core.Identity;
using ZavaFinance.One;
using Xunit;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ZavaFinance.Tests.One;

public sealed class FinanceActivityHostedTests
{
    private const string ServiceUrl = "https://connector.example.test/";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("adaptiveCard/action", DeliveryModes.Normal)]
    [InlineData("adaptiveCard/action", DeliveryModes.ExpectReplies)]
    [InlineData("adaptiveCard/action", DeliveryModes.Stream)]
    [InlineData("task/submit", DeliveryModes.Normal)]
    [InlineData("task/submit", DeliveryModes.ExpectReplies)]
    [InlineData("task/submit", DeliveryModes.Stream)]
    public async Task NativeWorkerDeliversAfterInvokeRequestScopeAndTokenAreGone(string format, string deliveryMode)
    {
        var probe = new Probe(format == "adaptiveCard/action");
        using IHost host = CreateHost(probe);
        var adapter = host.Services.GetRequiredService<CloudAdapter>();
        adapter.Use(new ObserveTurns(probe));
        adapter.OnTurnError = (_, error) =>
        {
            probe.Completed.TrySetException(error);
            return Task.FromException(error);
        };

        var previousHeaders = HeaderPropagationContext.HeadersFromRequest;
        // Deliberately seed the worker's inherited context; native dispatch must replace it.
        HeaderPropagationContext.HeadersFromRequest = new HeaderDictionary
        {
            ["x-ms-correlation-id"] = "inbound-correlation",
            ["Authorization"] = "Bearer test-only-inbound-transport"
        };
        try
        {
            await host.StartAsync();
            using var requestAborted = new CancellationTokenSource();
            ScopeMarker requestMarker;
            await using (AsyncServiceScope requestScope = host.Services.CreateAsyncScope())
            {
                requestMarker = requestScope.ServiceProvider.GetRequiredService<ScopeMarker>();
                HttpResult response = await DispatchInvokeAsync(adapter, requestScope.ServiceProvider,
                    CreateInvoke(format, deliveryMode), requestAborted.Token);
                Assert.True(probe.Errors.IsEmpty, string.Join(Environment.NewLine, probe.Errors));
                if (probe.Completed.Task.IsFaulted) await probe.Completed.Task;
                AssertHttpResponse(response, format, deliveryMode, 200, probe);
                Assert.Equal(200, Assert.Single(probe.Invokes).Status);
                Assert.Empty(probe.Posts);
                Assert.False(probe.ReleaseFinance.Task.IsCompleted);
            }

            Assert.True(requestMarker.Disposed.Task.IsCompletedSuccessfully);
            await requestAborted.CancelAsync();
            await Task.WhenAny(probe.FinanceEntered.Task, probe.Completed.Task).WaitAsync(Timeout);
            if (probe.Completed.Task.IsFaulted) await probe.Completed.Task;
            ScopeMarker workerMarker = await probe.FinanceEntered.Task.WaitAsync(Timeout);
            Assert.NotSame(requestMarker, workerMarker);
            Assert.False(workerMarker.Disposed.Task.IsCompleted);
            Assert.False(probe.WorkerToken.IsCancellationRequested);
            Assert.NotEqual(requestAborted.Token, probe.WorkerToken);
            Assert.Equal(new[] { requestMarker.Id, workerMarker.Id }, probe.Validations.ToArray());
            Assert.True(probe.TokenLookups.Count >= 2);

            TurnSnapshot inbound = Assert.Single(probe.Turns, turn => turn.Activity.Type == ActivityTypes.Invoke);
            TurnSnapshot queued = Assert.Single(probe.Turns, turn => turn.Activity.Type == ActivityTypes.Message);
            Assert.Equal(deliveryMode, inbound.Activity.DeliveryMode);
            Assert.Equal(DeliveryModes.Normal, queued.Activity.DeliveryMode);
            Assert.Empty(queued.Headers);
            Assert.NotSame(inbound.Connector, queued.Connector);
            Assert.NotSame(inbound.UserTokens, queued.UserTokens);
            Assert.Equal("channel-user-1", queued.Activity.From.Id);
            Assert.Equal("user-1", queued.Activity.From.AadObjectId);
            Assert.Equal("bot-1", queued.Activity.Recipient.Id);
            Assert.Equal(Channels.Msteams, queued.Activity.ChannelId);
            Assert.Equal("tenant-1", queued.Activity.Conversation.TenantId);
            Assert.Equal("tenant-1", Assert.IsType<JsonElement>(queued.Activity.ChannelData)
                .GetProperty("tenant").GetProperty("id").GetString());
            Assert.Equal(ServiceUrl, queued.Activity.ServiceUrl);

            probe.ReleaseFinance.SetResult();
            await probe.Completed.Task.WaitAsync(Timeout);
            await workerMarker.Disposed.Task.WaitAsync(Timeout);
            Assert.Empty(probe.Errors);
            Assert.Equal("connector-message-1", Assert.Single(probe.DeliveryIds));
            PostedActivity posted = Assert.Single(probe.Posts);
            Assert.Equal("connector.example.test", posted.Uri.Host);
            Assert.Contains("/conversations/conversation-1/activities/invoke-1", posted.Uri.AbsolutePath);
            Assert.Equal("conversation-1", posted.Activity.GetProperty("conversation").GetProperty("id").GetString());
            Assert.Equal("invoke-1", posted.Activity.GetProperty("replyToId").GetString());
            Assert.False(posted.HadAuthorization);
            if (probe.Card)
            {
                Assert.True(!posted.Activity.TryGetProperty("text", out var text) || text.ValueKind == JsonValueKind.Null);
                JsonElement attachment = Assert.Single(posted.Activity.GetProperty("attachments").EnumerateArray());
                Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.GetProperty("contentType").GetString());
            }
            else
            {
                Assert.Equal("Selected.", posted.Activity.GetProperty("text").GetString());
                Assert.True(!posted.Activity.TryGetProperty("attachments", out var attachments)
                    || attachments.ValueKind == JsonValueKind.Null || attachments.GetArrayLength() == 0);
            }
        }
        finally
        {
            probe.ReleaseFinance.TrySetResult();
            using var stopTimeout = new CancellationTokenSource(Timeout);
            try { await host.StopAsync(stopTimeout.Token); }
            finally { HeaderPropagationContext.HeadersFromRequest = previousHeaders; }
        }
    }

    public static IEnumerable<object[]> RejectedInvokes()
    {
        foreach (string format in new[] { "adaptiveCard/action", "task/submit" })
        foreach (string deliveryMode in new[] { DeliveryModes.Normal, DeliveryModes.ExpectReplies, DeliveryModes.Stream })
        foreach (string failure in new[] { "assertion", "malformed", "signin" })
            yield return [format, deliveryMode, failure];
    }

    [Theory]
    [MemberData(nameof(RejectedInvokes))]
    public async Task RejectedInvokeReturnsProtocolErrorWithoutFinanceOrBufferedReply(
        string format, string deliveryMode, string failure)
    {
        var probe = new Probe(card: false, failure);
        using IHost host = CreateHost(probe);
        var adapter = host.Services.GetRequiredService<CloudAdapter>();
        adapter.Use(new ObserveTurns(probe));
        adapter.OnTurnError = (_, error) =>
        {
            probe.Completed.TrySetException(error);
            return Task.FromException(error);
        };
        await host.StartAsync();
        try
        {
            await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
            Activity activity = CreateInvoke(format, deliveryMode);
            if (failure == "malformed")
            {
                activity.Value = new { hierarchyId = "untrusted-choice" };
                activity.Text = "reset";
            }
            HttpResult response = await DispatchInvokeAsync(adapter, scope.ServiceProvider, activity);
            AssertHttpResponse(response, format, deliveryMode, failure == "malformed" ? 400 : 401, probe);
            Assert.DoesNotContain("private-test", response.Body);
        }
        finally
        {
            probe.ReleaseFinance.TrySetResult();
            using var stopTimeout = new CancellationTokenSource(Timeout);
            await host.StopAsync(stopTimeout.Token);
        }
        Assert.Empty(probe.Errors);
        Assert.Empty(probe.Posts);
        Assert.Empty(probe.DeliveryIds);
        Assert.False(probe.FinanceEntered.Task.IsCompleted);
        Assert.Single(probe.Turns);
        Assert.Equal(failure == "signin" ? 0 : 1, probe.Validations.Count);
    }

    private static Activity CreateInvoke(string format, string deliveryMode) => new()
    {
        Type = ActivityTypes.Invoke, Name = format, Id = "invoke-1",
        DeliveryMode = deliveryMode, ChannelId = Channels.Msteams,
        ServiceUrl = ServiceUrl, Text = "Choose an organization.",
        Conversation = new ConversationAccount { Id = "conversation-1", TenantId = "tenant-1" },
        From = new ChannelAccount { Id = "channel-user-1", AadObjectId = "user-1" },
        Recipient = new ChannelAccount { Id = "bot-1" },
        ChannelData = JsonSerializer.SerializeToElement(new { tenant = new { id = "tenant-1" } }),
        Value = format == "adaptiveCard/action"
            ? new { action = new { type = "Action.Execute", data = FinanceActivityApplicationTests.Data() } }
            : new { data = FinanceActivityApplicationTests.Data() }
    };

    private static async Task<HttpResult> DispatchInvokeAsync(
        CloudAdapter adapter, IServiceProvider requestServices, Activity activity, CancellationToken cancellationToken = default)
    {
        using var requestBody = new MemoryStream(Encoding.UTF8.GetBytes(ProtocolJsonSerializer.ToJson(activity)));
        using var responseBody = new MemoryStream();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("aud", "bot-1")], "test-channel")),
            RequestServices = requestServices
        };
        http.Request.Method = HttpMethods.Post;
        http.Request.ContentType = "application/json";
        http.Request.ContentLength = requestBody.Length;
        http.Request.Body = requestBody;
        http.Request.Headers.Authorization = "Bearer test-only-inbound-transport";
        http.Response.Body = responseBody;
        await adapter.ProcessAsync(http.Request, http.Response,
            requestServices.GetRequiredService<FinanceActivityApplication>(), cancellationToken).WaitAsync(Timeout);
        return new(http.Response.StatusCode, http.Response.ContentType, Encoding.UTF8.GetString(responseBody.ToArray()));
    }

    private static void AssertHttpResponse(HttpResult response, string format, string deliveryMode, int status, Probe probe)
    {
        string diagnostic = response.Body + "\n" + string.Join("\n", probe.Errors);
        int invokeStatus = format == "adaptiveCard/action" ? 200 : status;
        bool stream = deliveryMode == DeliveryModes.Stream;
        Assert.True(response.StatusCode == (stream ? 200 : invokeStatus), diagnostic);
        Assert.NotNull(response.ContentType);
        Assert.StartsWith(stream ? "text/event-stream" : "application/json", response.ContentType);
        string json = response.Body;
        if (stream)
        {
            const string prefix = "event: invokeResponse\r\ndata: ";
            Assert.True(json.StartsWith(prefix, StringComparison.Ordinal), diagnostic);
            Assert.EndsWith("\r\n\r\n", json);
            json = json[prefix.Length..^4];
        }
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement body = document.RootElement;
        if (stream || deliveryMode == DeliveryModes.ExpectReplies)
        {
            if (stream) Assert.Equal(invokeStatus, body.GetProperty("status").GetInt32());
            else Assert.Empty(body.GetProperty("activities").EnumerateArray());
            Assert.True(body.TryGetProperty("body", out body), diagnostic);
        }
        if (format == "adaptiveCard/action")
        {
            Assert.True(body.TryGetProperty("statusCode", out JsonElement statusCode), diagnostic);
            Assert.Equal(status, statusCode.GetInt32());
            Assert.Equal(status == 200 ? ContentTypes.Message : ContentTypes.Error,
                body.GetProperty("type").GetString());
            Assert.Equal(3, body.EnumerateObject().Count());
            JsonElement value = body.GetProperty("value");
            if (status == 200) Assert.Equal("Request received.", value.GetString());
            else
            {
                Assert.Equal(status == 401 ? "Unauthorized" : "BadRequest", value.GetProperty("code").GetString());
                Assert.Equal("The choice could not be accepted. Please ask again.", value.GetProperty("message").GetString());
            }
        }
        else
        {
            // ProtocolJsonSerializer omits the null task; an activities envelope is not a task response.
            Assert.Empty(body.EnumerateObject());
        }
    }

    private static IHost CreateHost(Probe probe)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateScopes = true, ValidateOnBuild = true
        }));
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ErrorLogger(probe));
        builder.Services.AddSingleton(probe);
        builder.Services.AddSingleton<IStorage, MemoryStorage>();
        builder.Services.AddSingleton<IConnections, TestConnections>();
        builder.Services.AddSingleton<IChannelServiceClientFactory, TestChannelServices>();
        builder.Services.AddSingleton<IConfidentialClientApplication>(
            ConfidentialClientApplicationBuilder.Create("one-test-app").WithClientSecret("not-a-real-secret").Build());
        builder.Services.AddSingleton(new ActivityOptions());
        builder.Services.AddSingleton(new SessionKeyProvider("one-hosted-test-salt"));
        builder.Services.AddScoped<ScopeMarker>();
        builder.Services.AddScoped<IActivityAssertionValidator, ScopedValidator>();
        builder.Services.AddScoped<IFinanceActivityRunner, ScopedFinance>();
        builder.Services.AddScoped(sp =>
        {
            var storage = sp.GetRequiredService<IStorage>();
            var connections = sp.GetRequiredService<IConnections>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var authorization = new AzureBotUserAuthorization("mcs", storage, connections,
                new OAuthSettings { AzureBotOAuthConnectionName = "mcs" }, loggerFactory.CreateLogger<AzureBotUserAuthorization>());
            return new AgentApplicationOptions(storage, loggerFactory)
            {
                Proactive = new ProactiveOptions(storage),
                Connections = connections,
                UserAuthorization = new UserAuthorizationOptions(loggerFactory, storage, connections, [authorization]),
                StartTypingTimer = false
            };
        });
        builder.Services.AddFinanceActivity();
        builder.Services.AddAgent<FinanceActivityApplication, CloudAdapter>();
        return builder.Build();
    }

    private sealed class ScopeMarker : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() => Disposed.TrySetResult();
    }

    private sealed class ScopedValidator(Probe probe, ScopeMarker scope) : IActivityAssertionValidator
    {
        public Task<CallerIdentity> ValidateAsync(string? assertion, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(scope.Disposed.Task.IsCompleted);
            Assert.Equal(FinanceActivityApplicationTests.Fixture.Assertion, assertion);
            probe.Validations.Enqueue(scope.Id);
            if (probe.Failure == "assertion") throw new CallerIdentityException("private-test-invalid-assertion");
            return Task.FromResult(CallerIdentity.Create("tenant-1", "user-1"));
        }
    }

    private sealed class ScopedFinance(Probe probe, ScopeMarker scope) : IFinanceActivityRunner
    {
        public async Task<FinanceReply> RunReplyAsync(IDownstreamTokenProvider tokens, string sessionKey,
            string question, ClarificationSubmission? submission, CancellationToken cancellationToken)
        {
            Assert.IsType<OboTokenProvider>(tokens);
            Assert.Equal(new SessionKeyProvider("one-hosted-test-salt")
                .GetSessionKey(CallerIdentity.Create("tenant-1", "user-1"), "conversation-1"), sessionKey);
            Assert.Equal(new ClarificationSubmission("request-1", "org:east", "catalog-1"), submission);
            Assert.Equal(ClarificationCard.SubmissionQuestion, question);
            probe.WorkerToken = cancellationToken;
            probe.FinanceEntered.SetResult(scope);
            await probe.ReleaseFinance.Task.WaitAsync(cancellationToken);
            Assert.False(scope.Disposed.Task.IsCompleted);
            return new FinanceReply("Selected.", probe.Card
                ? new ClarificationPrompt("next-choice", "org", "Which organization?",
                    [new("org:east", "East"), new("org:west", "West")], "catalog-1")
                : null);
        }
    }

    private sealed class ObserveTurns(Probe probe) : Microsoft.Agents.Builder.IMiddleware
    {
        public async Task OnTurnAsync(ITurnContext context, NextDelegate next, CancellationToken cancellationToken)
        {
            probe.Turns.Enqueue(new(context.Activity.Clone(), context.Services.Get<IConnectorClient>(),
                context.Services.Get<IUserTokenClient>(), HeaderPropagationContext.HeadersFromRequest?.Keys.ToArray() ?? []));
            context.OnSendActivities(async (_, activities, send) =>
            {
                foreach (var activity in activities.Where(a => a.Type == ActivityTypes.InvokeResponse))
                    probe.Invokes.Enqueue(Assert.IsType<InvokeResponse>(activity.Value));
                ResourceResponse[] responses = await send();
                if (context.Activity.Type == ActivityTypes.Message)
                    foreach (var response in responses) probe.DeliveryIds.Enqueue(response.Id);
                return responses;
            });
            await next(cancellationToken);
            if (context.Activity.Type == ActivityTypes.Message) probe.Completed.TrySetResult();
        }
    }

    private sealed class TestChannelServices(Probe probe) : IChannelServiceClientFactory
    {
        public Task<IConnectorClient> CreateConnectorClientAsync(ClaimsIdentity identity, string serviceUrl,
            string audience, CancellationToken cancellationToken, IList<string>? scopes = null, bool useAnonymous = false)
            => Task.FromResult<IConnectorClient>(new RestConnectorClient(
                new Uri(serviceUrl), new TestHttpClients(probe), null, nameof(TestChannelServices)));

        public Task<IConnectorClient> CreateConnectorClientAsync(ITurnContext context, string? audience = null,
            IList<string>? scopes = null, bool useAnonymous = false, CancellationToken cancellationToken = default)
            => CreateConnectorClientAsync(context.Identity, context.Activity.ServiceUrl, audience!, cancellationToken, scopes, useAnonymous);

        public Task<IUserTokenClient> CreateUserTokenClientAsync(ClaimsIdentity identity, bool? useAnonymous = false,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("bot-1", identity.FindFirst("aud")?.Value);
            return Task.FromResult<IUserTokenClient>(new RestUserTokenClient("bot-1",
                new Uri("https://tokens.example.test/"), new TestHttpClients(probe), null,
                nameof(TestChannelServices), NullLogger.Instance));
        }
    }

    private sealed class TestHttpClients(Probe probe) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new TestTransport(probe));
    }

    private sealed class TestTransport(Probe probe) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = Assert.IsType<Uri>(request.RequestUri);
            Assert.Null(request.Headers.Authorization);
            if (uri.Host == "tokens.example.test")
            {
                if (probe.Failure == "signin")
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("""{"error":{"message":"private-test-signin-failure"}}""",
                            Encoding.UTF8, "application/json")
                    };
                Assert.Equal(HttpMethod.Get, request.Method);
                bool signIn = uri.AbsolutePath.EndsWith("/usertoken/GetTokenOrSignInResource", StringComparison.OrdinalIgnoreCase);
                Assert.True(signIn || uri.AbsolutePath.EndsWith("/usertoken/GetToken", StringComparison.OrdinalIgnoreCase),
                    $"Unexpected OAuth endpoint: {uri.AbsolutePath}");
                var query = QueryHelpers.ParseQuery(uri.Query);
                Assert.Equal("channel-user-1", query["userId"]);
                Assert.Equal("mcs", query["connectionName"]);
                Assert.Equal("msteams", query["channelId"]);
                probe.TokenLookups.Enqueue(uri);
                var token = new TokenResponse
                {
                    Token = FinanceActivityApplicationTests.Fixture.Assertion,
                    ConnectionName = "mcs", ChannelId = Channels.Msteams
                };
                object result = signIn ? new TokenOrSignInResourceResponse { TokenResponse = token } : token;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ProtocolJsonSerializer.ToJson(result), Encoding.UTF8, "application/json")
                };
            }

            Assert.Equal("connector.example.test", uri.Host);
            Assert.Equal(HttpMethod.Post, request.Method);
            using JsonDocument document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            probe.Posts.Enqueue(new(uri, document.RootElement.Clone(), request.Headers.Authorization is not null));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"connector-message-1"}""", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class TestConnections : IConnections
    {
        public IAccessTokenProvider GetConnection(string name) => throw new NotSupportedException();
        public bool TryGetConnection(string name, out IAccessTokenProvider provider) { provider = null!; return false; }
        public IAccessTokenProvider GetDefaultConnection() => throw new NotSupportedException();
        public IAccessTokenProvider GetTokenProvider(ClaimsIdentity identity, string serviceUrl) => throw new NotSupportedException();
        public IAccessTokenProvider GetTokenProvider(ClaimsIdentity identity, IActivity activity) => throw new NotSupportedException();
    }

    private sealed class Probe(bool card, string? failure = null)
    {
        public bool Card { get; } = card;
        public string? Failure { get; } = failure;
        public TaskCompletionSource<ScopeMarker> FinanceEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFinance { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<Guid> Validations { get; } = new();
        public ConcurrentQueue<TurnSnapshot> Turns { get; } = new();
        public ConcurrentQueue<InvokeResponse> Invokes { get; } = new();
        public ConcurrentQueue<Uri> TokenLookups { get; } = new();
        public ConcurrentQueue<PostedActivity> Posts { get; } = new();
        public ConcurrentQueue<string> DeliveryIds { get; } = new();
        public ConcurrentQueue<string> Errors { get; } = new();
        public CancellationToken WorkerToken { get; set; }
    }

    private sealed class ErrorLogger(Probe probe) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) probe.Errors.Enqueue($"{formatter(state, exception)} {exception}");
        }
        public void Dispose() { }
    }

    private sealed record TurnSnapshot(IActivity Activity, IConnectorClient Connector,
        IUserTokenClient UserTokens, string[] Headers);
    private sealed record PostedActivity(Uri Uri, JsonElement Activity, bool HadAuthorization);
    private sealed record HttpResult(int StatusCode, string? ContentType, string Body);
}
