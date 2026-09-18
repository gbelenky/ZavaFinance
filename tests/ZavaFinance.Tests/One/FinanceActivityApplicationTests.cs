using System.Security.Claims;
using System.Text.Json;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.App.Proactive;
using Microsoft.Agents.Builder.App.UserAuth;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Builder.UserAuth;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Hosting.AspNetCore.BackgroundQueue;
using Microsoft.Agents.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Client;
using ZavaFinance.Agent;
using ZavaFinance.Channel;
using ZavaFinance.Contracts;
using ZavaFinance.Core.Abstractions;
using ZavaFinance.Core.Identity;
using ZavaFinance.One;
using Xunit;

namespace ZavaFinance.Tests.One;

public sealed class FinanceActivityApplicationTests
{
    private static readonly ClarificationPrompt Prompt = new(
        "request-1", "org", "Which organization?",
        [new("org:east", "East"), new("org:west", "West")], "catalog-1");
    private static readonly ClarificationSubmission Submission = new("request-1", "org:east", "catalog-1");

    [Fact]
    public async Task RegisteredMessageAcquiresActualNamedSsoTokenAndUsesValidatedSessionAndObo()
    {
        using var fixture = new Fixture();
        await fixture.RunAsync("message", text: "  show revenue  ");
        Assert.True(fixture.Authorization.Calls > 0);
        Assert.Equal(Fixture.Assertion, Assert.Single(fixture.Validator.Assertions));
        Assert.Equal(1, fixture.Finance.Calls);
        Assert.IsType<OboTokenProvider>(fixture.Finance.Tokens);
        Assert.Equal(fixture.Keys.GetSessionKey(fixture.Validator.Caller, "conversation-1"), fixture.Finance.SessionKey);
        Assert.Equal("show revenue", fixture.Finance.Question);
        Assert.Null(fixture.Finance.Submission);
        AssertText(Assert.Single(fixture.Output.Activities), "Finance answer.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingAssertionCannotReachFinance(string? assertion)
    {
        using var fixture = new Fixture();
        fixture.Authorization.Token = assertion;
        await fixture.RunAsync("message");
        Assert.Equal(0, fixture.Finance.Calls);
        Assert.Empty(fixture.Validator.Assertions);
        if (string.IsNullOrEmpty(assertion))
            Assert.Empty(fixture.Output.Activities); // Auto-sign-in suspends the route while obtaining a token.
        else
            Assert.Contains("could not confirm", Assert.Single(fixture.Output.Messages));
    }

    [Theory]
    [InlineData("not-a-jwt", "message")]
    [InlineData("forged-signed-assertion", "adaptiveCard/action")]
    [InlineData("expired-assertion", "task/submit")]
    public async Task InvalidAssertionIsRejectedBeforeParsingOrFinance(string assertion, string format)
    {
        using var fixture = new Fixture();
        fixture.Authorization.Token = assertion;
        fixture.Validator.Reject = true;
        await fixture.RunAsync(format, "{malformed");
        Assert.Equal(assertion, Assert.Single(fixture.Validator.Assertions));
        Assert.Equal(0, fixture.Finance.Calls);
        if (format == "message")
            Assert.Contains("could not confirm", Assert.Single(fixture.Output.Messages));
        else
        {
            AssertInvoke(fixture.Output, format, 401);
            Assert.Equal(ActivityTypes.InvokeResponse, Assert.Single(fixture.Output.Activities).Type);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PayloadCannotChangeValidatedUserOrTenant(bool tenant)
    {
        using var fixture = new Fixture();
        fixture.Output.Activity.Conversation.TenantId = tenant ? "another-tenant" : "tenant-1";
        fixture.Output.Activity.From.AadObjectId = tenant ? "user-1" : "another-user";
        await fixture.RunAsync("message", Data());
        Assert.Equal(0, fixture.Finance.Calls);
        Assert.Contains("could not confirm", Assert.Single(fixture.Output.Messages));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingConversationCannotReachFinance(string conversation)
    {
        using var fixture = new Fixture();
        fixture.Output.Activity.Conversation.Id = conversation;
        await fixture.RunAsync("message");
        Assert.Equal(0, fixture.Finance.Calls);
        Assert.Contains("no identifier", Assert.Single(fixture.Output.Messages));
    }

    [Theory]
    [InlineData("message")]
    [InlineData("adaptiveCard/action")]
    [InlineData("task/submit")]
    public async Task MalformedSubmissionCannotFallBackToTextOrReset(string format)
    {
        using var fixture = new Fixture();
        await fixture.RunAsync(format, """{"hierarchyId":"other-user-choice"}""", "reset");
        Assert.Equal(0, fixture.Finance.Calls);
        if (format == "message")
            Assert.Contains("could not read that choice", Assert.Single(fixture.Output.Messages));
        else
        {
            AssertInvoke(fixture.Output, format, 400);
            Assert.Equal(ActivityTypes.InvokeResponse, Assert.Single(fixture.Output.Activities).Type);
        }
    }

    [Theory]
    [InlineData("adaptiveCard/action")]
    [InlineData("task/submit")]
    public async Task MissingInvokeSelectionIsRejected(string format)
    {
        using var fixture = new Fixture();
        await fixture.RunAsync(format);
        Assert.Equal(0, fixture.Finance.Calls);
        AssertInvoke(fixture.Output, format, 400);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("/reset")]
    [InlineData("cancel")]
    [InlineData("2")]
    [InlineData("East")]
    public async Task ResetAndTypedChoicesArePassedUnchangedToExistingOrchestrator(string text)
    {
        using var fixture = new Fixture();
        await fixture.RunAsync("message", text: text);
        Assert.Equal(text, fixture.Finance.Question);
        Assert.Null(fixture.Finance.Submission);
        Assert.Equal(1, fixture.Finance.Calls);
    }

    [Fact]
    public async Task ValidMessageSubmissionUsesSharedParserAndOverridesText()
    {
        using var fixture = new Fixture();
        await fixture.RunAsync("message", Data(), "reset");
        Assert.Equal(Submission, fixture.Finance.Submission);
        Assert.Equal(ClarificationCard.SubmissionQuestion, fixture.Finance.Question);
        Assert.Equal(1, fixture.Finance.Calls);
    }

    [Fact]
    public async Task ClarificationIsOneSharedClickableCardWithNoSiblingText()
    {
        using var fixture = new Fixture();
        fixture.Finance.Reply = new FinanceReply("Choose an organization.", Prompt);
        await fixture.RunAsync("message");
        IActivity activity = Assert.Single(fixture.Output.Activities);
        Assert.Equal(ActivityTypes.Message, activity.Type);
        Assert.True(string.IsNullOrEmpty(activity.Text));
        Attachment attachment = Assert.Single(activity.Attachments);
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.ContentType);
        JsonElement card = Assert.IsType<JsonElement>(attachment.Content);
        Assert.Equal(ClarificationCard.CreateJson(Prompt), card.GetRawText());
        JsonElement action = card.GetProperty("body")[2].GetProperty("selectAction");
        Assert.Equal("Action.Submit", action.GetProperty("type").GetString());
        Assert.Equal("org:east", action.GetProperty("data").GetProperty("optionId").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplyWithoutDeliveryAcknowledgementIsNotTreatedAsSuccess(bool card)
    {
        using var fixture = new Fixture();
        fixture.Output.MissingResourceId = true;
        fixture.Finance.Reply = new FinanceReply("Finance answer.", card ? Prompt : null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunAsync("message"));
        Assert.Single(fixture.Output.Activities);
        Assert.Equal(1, fixture.Finance.Calls);
    }

    [Fact]
    public async Task OrdinaryMessageAwaitsFinanceRatherThanStartingDetachedWork()
    {
        using var fixture = new Fixture();
        var completion = new TaskCompletionSource<FinanceReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Finance.Run = ct => completion.Task.WaitAsync(ct);
        Task turn = fixture.RunAsync("message");
        Assert.Equal(1, fixture.Finance.Calls);
        Assert.False(turn.IsCompleted);
        Assert.Empty(fixture.Output.Activities);
        completion.SetResult(new FinanceReply("Completed."));
        await turn;
        AssertText(Assert.Single(fixture.Output.Activities), "Completed.");
    }

    [Theory]
    [InlineData("adaptiveCard/action", DeliveryModes.Normal)]
    [InlineData("adaptiveCard/action", DeliveryModes.Stream)]
    [InlineData("adaptiveCard/action", DeliveryModes.ExpectReplies)]
    [InlineData("task/submit", DeliveryModes.Normal)]
    [InlineData("task/submit", DeliveryModes.Stream)]
    [InlineData("task/submit", DeliveryModes.ExpectReplies)]
    public async Task InvokeAcknowledgesBeforeSlowFinanceAndRedispatchesUsingM365NativeQueue(
        string format, string deliveryMode)
    {
        using var fixture = new Fixture();
        fixture.Output.Activity.DeliveryMode = deliveryMode;
        fixture.Output.Activity.ServiceUrl = "https://connector.example.test/";
        fixture.Output.Activity.Recipient = new ChannelAccount { Id = "one-bot" };
        fixture.Output.Activity.ReplyToId = "original-activity";
        var completion = new TaskCompletionSource<FinanceReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Finance.Run = ct => completion.Task.WaitAsync(ct);
        object value = format == "adaptiveCard/action"
            ? new { action = new { type = "Action.Execute", data = Data() } }
            : new { data = Data() };

        await fixture.RunAsync(format, value).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, fixture.Finance.Calls);
        AssertInvoke(fixture.Output, format, 200);
        Assert.Single(fixture.Output.Activities);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ActivityWithClaims queued = await fixture.Queue.WaitForActivityAsync(timeout.Token);
        Assert.Equal(typeof(FinanceActivityApplication), queued.AgentType);
        Assert.Equal(ActivityTypes.Message, queued.Activity.Type);
        Assert.Equal(DeliveryModes.Normal, queued.Activity.DeliveryMode);
        Assert.Equal(string.Empty, queued.Activity.Name);
        Assert.Equal(fixture.Output.Activity.Conversation.Id, queued.Activity.Conversation.Id);
        Assert.Equal(fixture.Output.Activity.Conversation.TenantId, queued.Activity.Conversation.TenantId);
        Assert.Equal(fixture.Output.Activity.From.Id, queued.Activity.From.Id);
        Assert.Equal(fixture.Output.Activity.From.AadObjectId, queued.Activity.From.AadObjectId);
        Assert.Equal("one-bot", queued.Activity.Recipient.Id);
        Assert.Equal("https://connector.example.test/", queued.Activity.ServiceUrl);
        Assert.Equal("original-activity", queued.Activity.ReplyToId);
        Assert.Same(fixture.Output.Identity, queued.ClaimsIdentity);
        Assert.Equal(Submission, ClarificationCard.ReadSubmission(queued.Activity));
        Assert.Equal(ActivityTypes.Invoke, fixture.Output.Activity.Type);
        Assert.Equal(deliveryMode, fixture.Output.Activity.DeliveryMode);
        Assert.DoesNotContain(Fixture.Assertion, JsonSerializer.Serialize(queued.Activity));

        using var context = new TurnContext(queued.ChannelAdapter, queued.Activity, queued.ClaimsIdentity);
        Task background = fixture.Application.OnTurnAsync(context, timeout.Token);
        Assert.False(background.IsCompleted);
        Assert.Equal(1, fixture.Finance.Calls);
        Assert.Equal(2, fixture.Validator.Assertions.Count);
        Assert.Equal(Submission, fixture.Finance.Submission);
        Assert.Equal(ClarificationCard.SubmissionQuestion, fixture.Finance.Question);
        completion.SetResult(new FinanceReply("Selected."));
        await background;
        Assert.Equal(2, fixture.Output.Activities.Count);
        AssertText(fixture.Output.Activities[1], "Selected.");
    }

    [Fact]
    public async Task ShutdownQueueCannotProduceSuccessfulInvokeAcceptance()
    {
        using var fixture = new Fixture();
        fixture.Queue.Stop(false);
        await fixture.RunAsync("adaptiveCard/action", new { action = new { type = "Action.Execute", data = Data() } });
        Assert.Equal(0, fixture.Finance.Calls);
        AssertInvoke(fixture.Output, "adaptiveCard/action", 503);
        Assert.Equal(ActivityTypes.InvokeResponse, Assert.Single(fixture.Output.Activities).Type);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationPropagatesWithoutErrorReply(bool duringFinance)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        if (duringFinance)
        {
            fixture.Finance.Run = ct =>
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Unreachable.");
            };
        }
        else cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.RunAsync("message", cancellationToken: cancellation.Token));
        Assert.Empty(fixture.Output.Activities);
    }

    [Theory]
    [InlineData("http", "finance service")]
    [InlineData("msal", "permission")]
    public async Task KnownDownstreamFailuresAreClassifiedWithoutLeakingDetails(string failure, string expected)
    {
        using var fixture = new Fixture();
        fixture.Finance.Run = _ => throw (failure == "http"
            ? new HttpRequestException("private-downstream-detail")
            : new MsalServiceException("denied", "private-downstream-detail"));
        await fixture.RunAsync("message");
        Assert.Contains(expected, Assert.Single(fixture.Output.Messages));
        Assert.DoesNotContain("private-downstream-detail", fixture.Output.Messages[0]);
    }

    [Fact]
    public async Task ProgrammingErrorsPropagateRatherThanBecomingSuccessShapedReplies()
    {
        using var fixture = new Fixture();
        fixture.Finance.Run = _ => throw new InvalidOperationException("Defect.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunAsync("message"));
        Assert.Empty(fixture.Output.Activities);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ProductionValidatorRejectsMissingAssertionsWithoutNetwork(string? assertion)
    {
        var validator = new ActivityAssertionValidator(new UserAssertionValidator(
            "tenant-1", "audience-1", NullLogger<UserAssertionValidator>.Instance));
        await Assert.ThrowsAsync<CallerIdentityException>(() =>
            validator.ValidateAsync(assertion, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task HandlerFailsClosedIfInvokedWithoutAnAssertion(string? assertion)
    {
        using var fixture = new Fixture();
        fixture.Authorization.Token = assertion;
        await fixture.RunAsync("message", autoSignIn: false);
        Assert.Empty(fixture.Validator.Assertions);
        Assert.Equal(0, fixture.Finance.Calls);
        Assert.Contains("could not confirm", Assert.Single(fixture.Output.Messages));
    }

    internal static object Data() => new
    {
        schema = FinanceReplyProtocol.Schema, action = ClarificationCard.SubmitAction,
        requestId = Submission.RequestId, optionId = Submission.OptionId, catalogVersion = Submission.CatalogVersion
    };

    private static void AssertText(IActivity activity, string expected)
    {
        Assert.Equal(ActivityTypes.Message, activity.Type);
        Assert.Equal(expected, activity.Text);
        Assert.True(activity.Attachments is null || activity.Attachments.Count == 0);
    }

    private static void AssertInvoke(ChannelTestTurnContext output, string format, int status)
    {
        var response = Assert.IsType<InvokeResponse>(
            Assert.Single(output.Activities, a => a.Type == ActivityTypes.InvokeResponse).Value);
        Assert.Equal(format == "adaptiveCard/action" ? 200 : status, response.Status);
        if (format == "adaptiveCard/action")
        {
            var body = Assert.IsType<AdaptiveCardInvokeResponse>(response.Body);
            Assert.Equal(status, body.StatusCode);
            Assert.Equal(status == 200
                ? "application/vnd.microsoft.activity.message"
                : "application/vnd.microsoft.error", body.Type);
            if (status == 200) Assert.Equal("Request received.", Assert.IsType<string>(body.Value));
        }
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _services = new ServiceCollection().AddAsyncAdapterSupport().BuildServiceProvider();
        public const string Assertion = "unit-test-sso-assertion-not-a-credential";
        public ChannelTestTurnContext Output { get; } = new();
        public TestAuthorization Authorization { get; } = new();
        public TestValidator Validator { get; } = new();
        public TestFinance Finance { get; } = new();
        public SessionKeyProvider Keys { get; } = new("one-test-salt");
        public IActivityTaskQueue Queue { get; }
        public FinanceActivityApplication Application { get; }

        public Fixture(IFinanceActivityRunner? finance = null)
        {
            Queue = _services.GetRequiredService<IActivityTaskQueue>();
            var storage = new MemoryStorage();
            var connections = new TestConnections();
            var options = new AgentApplicationOptions(storage, NullLoggerFactory.Instance)
            {
                Proactive = new ProactiveOptions(storage),
                Connections = connections,
                UserAuthorization = new UserAuthorizationOptions(
                    NullLoggerFactory.Instance, storage, connections, [Authorization]),
                StartTypingTimer = false
            };
            Application = new FinanceActivityApplication(options, Validator, finance ?? Finance,
                ConfidentialClientApplicationBuilder.Create("one-test-app").WithClientSecret("not-a-real-secret").Build(),
                Keys, new ChannelOptions(), Queue, NullLogger<FinanceActivityApplication>.Instance);
        }

        public async Task RunAsync(
            string format, object? value = null, string text = "Finance question.",
            CancellationToken cancellationToken = default, bool autoSignIn = true)
        {
            Output.Activity.Type = format == "message" ? ActivityTypes.Message : ActivityTypes.Invoke;
            Output.Activity.Name = format;
            Output.Activity.Text = text;
            Output.Activity.Value = value!;
            using var context = new TurnContext(new TestAdapter(Output), Output.Activity, Output.Identity);
            if (autoSignIn) await Application.OnTurnAsync(context, cancellationToken);
            else await Application.OnMessageAsync(context, new TurnState(), cancellationToken);
        }

        public void Dispose() => _services.Dispose();
    }

    internal sealed class TestValidator : IActivityAssertionValidator
    {
        public CallerIdentity Caller { get; set; } = CallerIdentity.Create("tenant-1", "user-1");
        public List<string?> Assertions { get; } = [];
        public bool Reject { get; set; }
        public Task<CallerIdentity> ValidateAsync(string? assertion, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assertions.Add(assertion);
            if (Reject) throw new CallerIdentityException("Validation refused.");
            return Task.FromResult(Caller);
        }
    }

    internal sealed class TestFinance : IFinanceActivityRunner
    {
        public int Calls { get; private set; }
        public IDownstreamTokenProvider? Tokens { get; private set; }
        public string? SessionKey { get; private set; }
        public string? Question { get; private set; }
        public ClarificationSubmission? Submission { get; private set; }
        public FinanceReply Reply { get; set; } = new("Finance answer.");
        public Func<CancellationToken, Task<FinanceReply>>? Run { get; set; }
        public Task<FinanceReply> RunReplyAsync(IDownstreamTokenProvider tokenProvider,
            string sessionKey, string question, ClarificationSubmission? submission, CancellationToken cancellationToken)
        {
            Calls++;
            Tokens = tokenProvider;
            SessionKey = sessionKey;
            Question = question;
            Submission = submission;
            return Run?.Invoke(cancellationToken) ?? Task.FromResult(Reply);
        }
    }

    internal sealed class TestAuthorization : IUserAuthorization
    {
        public string Name => "mcs";
        public string? Token { get; set; } = Fixture.Assertion;
        public int Calls { get; private set; }
        public Task<TokenResponse> SignInUserAsync(ITurnContext context, bool forceSignIn, string exchangeToken,
            IList<string> scopes, CancellationToken cancellationToken) => GetToken(cancellationToken);
        public Task<TokenResponse> GetRefreshedUserTokenAsync(ITurnContext context, string exchangeToken,
            IList<string> scopes, CancellationToken cancellationToken) => GetToken(cancellationToken);
        public Task ResetStateAsync(ITurnContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SignOutUserAsync(ITurnContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        private Task<TokenResponse> GetToken(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new TokenResponse { Token = Token! });
        }
    }

    private sealed class TestAdapter(ChannelTestTurnContext output) : ChannelAdapter
    {
        public override async Task<ResourceResponse[]> SendActivitiesAsync(
            ITurnContext context, IActivity[] activities, CancellationToken cancellationToken)
        {
            var responses = new List<ResourceResponse>();
            foreach (IActivity activity in activities)
                responses.Add(await output.SendActivityAsync(activity, cancellationToken));
            return responses.ToArray();
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
}
