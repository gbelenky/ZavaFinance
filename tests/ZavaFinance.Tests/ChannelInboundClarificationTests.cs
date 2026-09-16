using System.Text.Json;
using System.Security.Claims;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.App.Proactive;
using Microsoft.Agents.Builder.App.UserAuth;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Builder.UserAuth;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ZavaFinance.Channel;
using ZavaFinance.Contracts;
using ZavaFinance.Core.Identity;
using Xunit;

namespace ZavaFinance.Tests;

public sealed class ChannelInboundClarificationTests
{
    [Theory]
    [InlineData("message")]
    [InlineData("adaptiveCard/action")]
    public async Task RegisteredTextAndInvokeRoutesBothRequireTheNamedSsoHandler(string format)
    {
        var storage = new ChannelTestStorage();
        var identity = new TestIdentity();
        var scheduler = new TestScheduler();
        var auth = new TestAuthorization();
        var data = new
        {
            schema = FinanceReplyProtocol.Schema, action = ClarificationCard.SubmitAction,
            requestId = "request-1", optionId = "org:choice", catalogVersion = "version-1"
        };
        var context = Context(format, format == "message"
            ? data : new { action = new { type = "Action.Execute", data } });
        using var routed = new TurnContext(new TestAdapter(context), context.Activity, context.Identity);
        await Create(storage, identity, scheduler, auth).OnTurnAsync(routed, CancellationToken.None);
        Assert.True(auth.Calls > 0);
        Assert.Equal(1, identity.Calls);
        Assert.Equal(1, scheduler.Calls);
    }

    [Theory]
    [InlineData("message")]
    [InlineData("adaptiveCard/action")]
    [InlineData("task/submit")]
    public async Task SubmitIdentityRejectionHappensBeforePayloadParsingOrStorage(string format)
    {
        var storage = new ChannelTestStorage();
        var identity = new TestIdentity { Reject = true };
        var scheduler = new TestScheduler();
        OrchestratorChannel channel = Create(storage, identity, scheduler);
        var context = Context(format, "{malformed");
        await Dispatch(channel, context);
        Assert.Equal(1, identity.Calls);
        Assert.Empty(storage.Items);
        Assert.Equal(0, scheduler.Calls);
        Assert.Contains("I could not verify your identity for this conversation.", context.Messages);
        if (format != "message")
        {
            IActivity response = context.Activities.Single(a => a.Type == ActivityTypes.InvokeResponse);
            var invoke = Assert.IsType<InvokeResponse>(response.Value);
            Assert.Equal(200, invoke.Status);
        }
    }

    [Theory]
    [InlineData("message")]
    [InlineData("adaptiveCard/action")]
    public async Task MalformedAuthenticatedSelectionCannotFallBackToText(string format)
    {
        var storage = new ChannelTestStorage();
        var identity = new TestIdentity();
        var scheduler = new TestScheduler();
        OrchestratorChannel channel = Create(storage, identity, scheduler);
        var context = Context(format, """{"hierarchyId":"123"}""");
        context.Activity.Text = "reset";
        await Dispatch(channel, context);
        Assert.Equal(1, identity.Calls);
        Assert.Empty(storage.Items);
        Assert.Equal(0, scheduler.Calls);
        Assert.Contains(context.Messages, m => m is not null && m.Contains("could not read that choice", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResetStartsANewSessionWithoutRemovingReplayTombstones()
    {
        var storage = new ChannelTestStorage();
        var store = new ChannelSessionStore(storage);
        string key = new SessionKeyProvider("channel-test").GetSessionKey(TestIdentity.Caller, "conversation-1");
        await store.SaveAsync(key, new ChannelSessionState { HostedAgentConversationId = "conv_old" }, CancellationToken.None);
        await store.CreateTurnAsync(key, "delivered", "old question", CancellationToken.None);
        var pending = (await store.ReadTurnAsync(key, "delivered", CancellationToken.None))!;
        await store.SaveAnswerAsync(key, "delivered", pending, "old answer", CancellationToken.None);
        await store.MarkDeliveredAsync(key, "delivered",
            (await store.ReadTurnAsync(key, "delivered", CancellationToken.None))!, CancellationToken.None);
        var context = Context("message", null);
        context.Activity.Text = "reset";
        await Dispatch(Create(storage, new TestIdentity(), new TestScheduler()), context);
        Assert.Null((await store.LoadAsync(key, CancellationToken.None)).HostedAgentConversationId);
        Assert.Equal(TurnDeliveryStatus.Delivered,
            (await store.ReadTurnAsync(key, "delivered", CancellationToken.None))!.Status);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("North branch")]
    [InlineData("cancel")]
    public async Task TextIsForwardedForAgentResolutionNotConvertedToCardSelection(string text)
    {
        var storage = new ChannelTestStorage();
        var identity = new TestIdentity();
        var scheduler = new TestScheduler();
        var context = Context("message", null);
        context.Activity.Text = text;
        await Dispatch(Create(storage, identity, scheduler), context);
        Assert.Equal(1, identity.Calls);
        TurnDeliveryRecord pending = (await new ChannelSessionStore(storage).ReadTurnAsync(
            scheduler.LastRequest!.SessionKey, scheduler.LastRequest.TurnId, CancellationToken.None))!;
        Assert.Equal(text, pending.Question);
        Assert.Null(pending.Submission);
    }

    [Theory]
    [InlineData("message")]
    [InlineData("adaptiveCard/action")]
    [InlineData("task/submit")]
    public async Task ValidSelectionSchedulesIdentifiersOnlyAndPreservesPendingSelection(string format)
    {
        var storage = new ChannelTestStorage();
        var identity = new TestIdentity();
        var scheduler = new TestScheduler();
        var data = new
        {
            schema = FinanceReplyProtocol.Schema, action = ClarificationCard.SubmitAction,
            requestId = "request-1", optionId = "org:untrusted", catalogVersion = "version-1"
        };
        object value = format switch
        {
            "adaptiveCard/action" => new { action = new { type = "Action.Execute", data } },
            "task/submit" => new { data },
            _ => data
        };
        var context = Context(format, value);
        context.Activity.Text = "reset";
        OrchestratorChannel channel = Create(storage, identity, scheduler);
        await Dispatch(channel, context);
        await Dispatch(channel, context);
        Assert.Equal(2, identity.Calls);
        Assert.Equal(2, scheduler.Calls);
        Assert.DoesNotContain("request-1", JsonSerializer.Serialize(scheduler.LastRequest));
        Assert.DoesNotContain("org:untrusted", JsonSerializer.Serialize(scheduler.LastRequest));
        Assert.DoesNotContain("assertion", JsonSerializer.Serialize(storage.Items), StringComparison.OrdinalIgnoreCase);
        TurnDeliveryRecord pending = (await new ChannelSessionStore(storage).ReadTurnAsync(
            scheduler.LastRequest!.SessionKey, scheduler.LastRequest.TurnId, CancellationToken.None))!;
        Assert.Equal(TurnDeliveryStatus.Pending, pending.Status);
        Assert.Equal(ClarificationCard.SubmissionQuestion, pending.Question);
        Assert.Equal(new ClarificationSubmission("request-1", "org:untrusted", "version-1"), pending.Submission);
        Assert.Single(context.Messages, m => m == "Working on that…");
    }

    private static ChannelTestTurnContext Context(string format, object? value)
    {
        var context = new ChannelTestTurnContext();
        context.Activity.Type = format == "message" ? ActivityTypes.Message : ActivityTypes.Invoke;
        context.Activity.Name = format;
        context.Activity.Value = value!;
        return context;
    }

    private static Task Dispatch(OrchestratorChannel channel, ChannelTestTurnContext context)
        => context.Activity.Type == ActivityTypes.Message
            ? channel.OnMessageAsync(context, new TurnState(), CancellationToken.None)
            : channel.OnClarificationInvokeAsync(context, new TurnState(), CancellationToken.None);

    private static OrchestratorChannel Create(ChannelTestStorage storage, TestIdentity identity, TestScheduler scheduler,
        TestAuthorization? authorization = null)
    {
        var store = new ChannelSessionStore(storage);
        var stateStorage = new MemoryStorage();
        var connections = new TestConnections();
        var options = new AgentApplicationOptions(stateStorage, NullLoggerFactory.Instance)
        {
            Proactive = new ProactiveOptions(stateStorage),
            Connections = connections,
            UserAuthorization = new UserAuthorizationOptions(
                NullLoggerFactory.Instance, stateStorage, connections, [authorization ?? new TestAuthorization()]),
            StartTypingTimer = false
        };
        return new OrchestratorChannel(options, identity, new SessionKeyProvider("channel-test"),
            scheduler, new ChannelTurnProcessor(store, new UnexpectedHostedAgent(),
                new FakeTimeProvider(), NullLogger<ChannelTurnProcessor>.Instance),
            store, new ChannelOptions(), NullLogger<OrchestratorChannel>.Instance);
    }

    private sealed class TestAuthorization : IUserAuthorization
    {
        public string Name => "mcs";
        public int Calls { get; private set; }
        public Task<TokenResponse> SignInUserAsync(ITurnContext context, bool forceSignIn, string exchangeToken,
            IList<string> scopes, CancellationToken ct) => Token();
        public Task<TokenResponse> GetRefreshedUserTokenAsync(ITurnContext context, string exchangeToken,
            IList<string> scopes, CancellationToken ct) => Token();
        public Task ResetStateAsync(ITurnContext context, CancellationToken ct) => Task.CompletedTask;
        public Task SignOutUserAsync(ITurnContext context, CancellationToken ct) => Task.CompletedTask;
        private Task<TokenResponse> Token()
        {
            Calls++;
            return Task.FromResult(new TokenResponse { Token = "test-sso-assertion" });
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

    private sealed class TestAdapter(ChannelTestTurnContext output) : ChannelAdapter
    {
        public override async Task<ResourceResponse[]> SendActivitiesAsync(
            ITurnContext turnContext, IActivity[] activities, CancellationToken cancellationToken)
        {
            var responses = new List<ResourceResponse>();
            foreach (IActivity activity in activities)
            {
                responses.Add(await output.SendActivityAsync(activity, cancellationToken));
            }
            return responses.ToArray();
        }
    }

    private sealed class TestIdentity : ICallerIdentityResolver
    {
        public static readonly CallerIdentity Caller = CallerIdentity.Create("tenant-1", "user-1");
        public bool Reject { get; init; }
        public int Calls { get; private set; }
        public Task<CallerIdentity> ResolveAsync(ITurnContext context, UserAuthorization authorization, CancellationToken ct)
        {
            Calls++;
            if (Reject) { throw new CallerIdentityException("Rejected test identity."); }
            return Task.FromResult(Caller);
        }
    }

    private sealed class TestScheduler : IOrchestratorTurnScheduler
    {
        public int Calls { get; private set; }
        public OrchestratorTurnRequest? LastRequest { get; private set; }
        public Task<string> ScheduleAsync(string sessionKey, string turnId, string conversationRecordId,
            string channelId, CancellationToken ct)
        {
            Calls++;
            LastRequest = new OrchestratorTurnRequest
            {
                SessionKey = sessionKey, TurnId = turnId, ConversationRecordId = conversationRecordId,
                ChannelId = channelId, IdempotencyKey = $"{sessionKey}:{turnId}"
            };
            return Task.FromResult($"turn-{sessionKey}-{turnId}");
        }
    }

    private sealed class UnexpectedHostedAgent : IHostedAgentClient
    {
        public Task<string> CreateConversationAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FinanceReply> AskAsync(string question, string userAssertion, string? conversationId,
            CancellationToken cancellationToken, ClarificationSubmission? submission = null) => throw new NotSupportedException();
    }
}
